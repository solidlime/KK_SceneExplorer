using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using BepInEx.Logging;

namespace KK_SceneExplorer
{
	/// <summary>
	/// MaterialEditor（KK_MaterialEditor.dll）テクスチャのシーン横断 dedupe。
	/// ME 側は無改造・参照もしない（全リフレクション）。
	///
	/// 仕組み:
	///  ME は保存時に TextureSaveHandler.SaveBundled で
	///  Dictionary&lt;int, TextureContainer&gt; を Dictionary&lt;int, byte[]&gt;(=Data) に落として
	///  MessagePack でシーン/カードの ESF に書き込む（ME 側は Hash を保存していない）。
	///  そこで SaveBundled の実行中だけ各 TextureContainer の private _token を
	///  「元データの Hash を埋め込んだ 16 バイトのマーカー」の token に差し替え、
	///  実データはシーンファイル末尾の dedupe チャンクへ 1 部だけ書く。
	///
	///  _token を直接差し替えるのは、Data setter が Release→Acquire で
	///  実テクスチャを破棄しうるため（差し替え中に Texture を読まれると壊れる）。
	///
	/// チャンク（ファイル末尾に追記）:
	///   "KKSEDUP1"(8) count(u32) { hash(i64) len(u32) data }*  len(u32) "KKSEDUP1"(8)
	///   先頭にのみ置くと開始位置が不明なので、末尾に長さ+magic のフッタを置いて O(1) で辿る。
	///   フッタが無い/壊れているファイルはバニラとして完全に素通りする。
	/// </summary>
	internal static class MeTextureDedupe
	{
		private static readonly byte[] ChunkMagic = Encoding.ASCII.GetBytes("KKSEDUP1");
		private static readonly byte[] MarkerMagic = Encoding.ASCII.GetBytes("KKSMEDD1");
		private const int ChunkHeaderSize = 12;   // magic(8) + count(4)
		private const int ChunkFooterSize = 12;   // payloadLen(4) + magic(8)
		private const int EntryHeaderSize = 12;   // hash(8) + len(4)
		private const int MarkerSize = 16;        // magic(8) + hash(8)

		private static ManualLogSource Log { get { return SceneExplorerPlugin.Log; } }

		// net35 に FileStream.Flush(bool) が無いため、転送層と同じ kernel32 の FlushFileBuffers を使う
		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool FlushFileBuffers(IntPtr hFile);

		// TextureContainer の反射ハンドル（実型は辞書の値から取る）
		private static Type containerType;
		private static PropertyInfo dataProp;
		private static PropertyInfo hashProp;
		private static FieldInfo tokenField;
		private static ConstructorInfo containerCtor;

		// 保存中（>0 のときだけ SaveBundled を書き換える。キャラカード保存を巻き込まないためのゲート）
		private static int savingDepth;
		// savingDepth を立てたフレーム。Tick はフレームが変わってなお残っている場合だけリークと見なす
		// （同一フレームなら別の SaveBundled が正当に走っている可能性がある）
		private static int saveStartedFrame = -1;
		private static readonly Dictionary<long, byte[]> pendingBlob = new Dictionary<long, byte[]>();
		private static readonly Dictionary<long, object> markerTokens = new Dictionary<long, object>();

		// 変換結果のキャッシュ（hash -> WebP バイト列）。再起動で消えるが、連続保存では効く
		private static readonly Dictionary<long, byte[]> webpCache = new Dictionary<long, byte[]>();
		// PNG -> WebP(ロスレス) 変換。null = 変換しない（PNG のまま格納）
		private static Func<byte[], byte[]> webpEncoder;
		// バックグラウンド変換の待ち行列（FIFO）。Update ごとに1枚だけ処理する
		private static readonly Queue<KeyValuePair<long, byte[]>> convertQueue = new Queue<KeyValuePair<long, byte[]>>();
		// 変換待ちの重複登録を防ぐ
		private static readonly HashSet<long> convertQueued = new HashSet<long>();

		// 直近に読み込んだシーンの共有表。null = チャンク無し（バニラ）
		private static Dictionary<long, byte[]> loadedBlob;
		// ログ用（"どのシーンの話か"）。ロードの可否判定には使わない
		private static string loadedPath;

		/// <summary>PNG を WebP に変換する関数を差し込む。null で無効化（PNG のまま格納）。</summary>
		internal static void SetWebPEncoder(Func<byte[], byte[]> encoder)
		{
			webpEncoder = encoder;
			ClearWebPCache();
		}

		/// <summary>変換済みなら WebP、未変換なら PNG をそのまま返す（保存を止めない）。</summary>
		private static byte[] ToWebP(long hash, byte[] png)
		{
			byte[] cached;
			lock (convertLock)
			{
				if (webpCache.TryGetValue(hash, out cached)) return cached;
			}
			return png;
		}

		/// <summary>変換待ち枚数（診断用）。</summary>
		internal static int PendingConvertCount { get { lock (convertLock) return convertQueue.Count; } }

		/// <summary>変換結果と待ち行列を捨てる。</summary>
		internal static void ClearWebPCache()
		{
			lock (convertLock)
			{
				webpCache.Clear();
				convertQueue.Clear();
				convertQueued.Clear();
			}
		}

		#region 保存

		internal static void BeginSceneSave(int frame)
		{
			if (savingDepth == 0)
			{
				pendingBlob.Clear();
				saveStartedFrame = frame;
			}
			savingDepth++;
		}

		/// <summary>SceneInfo.Save の finalizer 用。例外で postfix が飛んでもゲートを必ず戻す。</summary>
		internal static void AbortSceneSave()
		{
			savingDepth = 0;
			pendingBlob.Clear();
		}

		/// <summary>SaveBundled prefix: 実データを退避して _token をマーカー token に差し替える。
		/// SaveBundled は dictRaw を Dictionary<int, TextureContainer> として具体型で要求するため、
		/// 配列を差し替えてはいけない（辞書そのものを書き換える）。</summary>
		internal static List<object[]> SwapToMarkers(object dictRaw)
		{
			if (savingDepth <= 0) return null;
			IDictionary dict = dictRaw as IDictionary;
			if (dict == null) return null;

			object sample = null;
			foreach (DictionaryEntry entry in dict)
			{
				if (entry.Value != null) { sample = entry.Value; break; }
			}
			if (sample == null) return null;
			if (!EnsureReflection(sample.GetType())) return null;

			List<object[]> state = new List<object[]>();
			foreach (DictionaryEntry entry in dict)
			{
				object container = entry.Value;
				if (container == null) continue;
				try
				{
					byte[] data = (byte[])dataProp.GetValue(container, null);
					// 空 / 既にマーカー / local texture 参照（Data が null）は触らない
					if (data == null || data.Length < MarkerSize) continue;
					if (IsMarker(data)) continue;

					long hash = (long)hashProp.GetValue(container, null);
					object markerToken = GetMarkerToken(hash);
					if (markerToken == null) continue;
					// 格納するのは WebP（ロスレス）。ロード時は LoadTexture2DFromBytes が WebP を判別する
					// hash 衝突時は共有表を汚さず、このテクスチャはインライン（マーカー化しない）で保存する
					if (!TryRegisterBlob(pendingBlob, hash, ToWebP(hash, data))) continue;

					object originalToken = tokenField.GetValue(container);
					tokenField.SetValue(container, markerToken);
					state.Add(new object[] { container, originalToken });
				}
				catch (Exception ex)
				{
					Log.LogWarning("[SceneExplorer] ME texture dedupe: 差し替え失敗（そのテクスチャはそのまま保存）: " + ex.Message);
				}
			}
			return state.Count > 0 ? state : null;
		}

		/// <summary>hash 衝突していないことを確認して共有表へ登録する。衝突時は表を汚さず false。</summary>
		internal static bool TryRegisterBlob(Dictionary<long, byte[]> table, long hash, byte[] data)
		{
			byte[] existing;
			if (!table.TryGetValue(hash, out existing))
			{
				table.Add(hash, data);
				return true;
			}
			if (BytesEqual(existing, data)) return true;

			Log.LogWarning("[SceneExplorer] ME texture dedupe: hash 衝突を検出（上書きせず、そのテクスチャはインラインで保存します）: hash=" + hash);
			return false;
		}

		internal static bool BytesEqual(byte[] a, byte[] b)
		{
			if (ReferenceEquals(a, b)) return true;
			if (a == null || b == null || a.Length != b.Length) return false;
			for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
			return true;
		}

		/// <summary>SaveBundled finalizer: 例外時も含めて必ず実データへ戻す。</summary>
		/// <summary>WebP 未変換のテクスチャをバックグラウンド変換の待ち行列へ入れる。</summary>
		private static void QueueConvert(Dictionary<long, byte[]> table)
		{
			if (webpEncoder == null || table == null) return;
			int added = 0;
			lock (convertLock)
			{
				foreach (KeyValuePair<long, byte[]> kv in table)
				{
					if (webpCache.ContainsKey(kv.Key) || !convertQueued.Add(kv.Key)) continue;
					if (IsWebP(kv.Value)) { convertQueued.Remove(kv.Key); webpCache[kv.Key] = kv.Value; continue; }
					convertQueue.Enqueue(kv);
					added++;
				}
			}
			if (added > 0)
			{
				Log.LogInfo("[SceneExplorer] ME texture dedupe: WebP 変換を " + added + " 枚キューに追加（別スレッドで進めます）");
				PumpConvert();
			}
		}

		/// <summary>RIFF....WEBP 判定。</summary>
		private static bool IsWebP(byte[] d)
		{
			return d != null && d.Length > 12 && d[0] == 0x52 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x46
				&& d[8] == 0x57 && d[9] == 0x45 && d[10] == 0x42 && d[11] == 0x50;
		}

		internal static void RestoreMarkers(List<object[]> state)
		{
			if (state == null) return;
			for (int i = 0; i < state.Count; i++)
			{
				try { tokenField.SetValue(state[i][0], state[i][1]); }
				catch (Exception ex) { Log.LogWarning("[SceneExplorer] ME texture dedupe: 復元失敗: " + ex.Message); }
			}
		}

		/// <summary>毎フレームのリーク検査。SceneInfo.Save は 1 コールスタックで完結するので、フレームを跨いで
		/// savingDepth が残っていたら定義上リーク（高優先度 prefix が本体を止めた等）。
		/// 放置すると次のキャラカード保存がマーカー化されてテクスチャが恒久消失する。</summary>
		// ワーカースレッドが動いているか（0/1）
		private static int convertInFlight;
		// キューとキャッシュはワーカースレッドから触るので全てこのロックで保護する
		private static readonly object convertLock = new object();

		/// <summary>待ち行列が空になるまでワーカースレッドで変換する。ゲームスレッドは一切ブロックしない。</summary>
		private static void PumpConvert()
		{
			if (webpEncoder == null) return;
			if (Interlocked.CompareExchange(ref convertInFlight, 1, 0) != 0) return;

			ThreadPool.QueueUserWorkItem(delegate(object _)
			{
				try
				{
					while (true)
					{
						KeyValuePair<long, byte[]> item;
						lock (convertLock)
						{
							if (convertQueue.Count == 0) break;
							item = convertQueue.Dequeue();
							convertQueued.Remove(item.Key);
							if (webpCache.ContainsKey(item.Key)) continue;
						}

						byte[] webp = webpEncoder(item.Value);
						if (webp != null && webp.Length < item.Value.Length)
						{
							lock (convertLock) webpCache[item.Key] = webp;
						}
					}

					int done;
					lock (convertLock) done = webpCache.Count;
					Log.LogInfo("[SceneExplorer] ME texture dedupe: WebP 変換が完了（" + done + " 枚）。次回の保存からサイズが減ります");
				}
				catch (Exception ex)
				{
					Log.LogWarning("[SceneExplorer] ME texture dedupe: WebP 変換に失敗（PNG で保存します）: " + ex.Message);
					webpEncoder = null;
					lock (convertLock) { convertQueue.Clear(); convertQueued.Clear(); }
				}
				finally { Interlocked.Exchange(ref convertInFlight, 0); }
			});
		}

		internal static void Tick(int frame)
		{
			if (savingDepth <= 0) return;
			// 同一フレーム内は正当な保存が進行中かもしれないので触らない
			if (frame == saveStartedFrame) return;
			savingDepth = 0;
			pendingBlob.Clear();
			Log.LogWarning("[SceneExplorer] ME texture dedupe: シーン保存が完了しないままフレームを跨いだためゲートをリセット（保存は中断された可能性）");
		}

		/// <summary>SceneInfo.Save postfix: 末尾に dedupe チャンクを追記する。
		/// 戻り値 true = 保存済みファイルが正しい dedupe 状態（対象 0 枚 or 追記 + 再読取検証済）。</summary>
		internal static bool EndSceneSave(string path)
		{
			savingDepth = 0;
			try
			{
				if (pendingBlob.Count == 0)
				{
					Log.LogInfo("[SceneExplorer] ME texture dedupe: 対象テクスチャ無し（チャンク不要 = バニラファイル）");
					return true;
				}
				if (string.IsNullOrEmpty(path) || !File.Exists(path))
				{
					Log.LogError("[SceneExplorer] ME texture dedupe: 保存先シーンファイルが見つからないためテクスチャを書き込めなかった（このシーンのテクスチャは復元できません）: " + path);
					return false;
				}

				using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
				{
					if (!WriteChunk(fs, path)) return false;
					// OS キャッシュ止まりでは検証が「同じキャッシュを読み直した」以上の意味を持たない
					fs.Flush();
#pragma warning disable 618 // net35 の SafeFileHandle は DangerousGetHandle 経由になり、転送層と揃わない
					if (!FlushFileBuffers(fs.Handle))
					{
						Log.LogWarning("[SceneExplorer] ME texture dedupe: FlushFileBuffers に失敗（追記は継続）: " + path);
					}
#pragma warning restore 618
				}

				// 追記したものを読み直して検証（データ本体は読まずヘッダだけ）
				if (!VerifyChunk(path)) return false;
				return true;
			}
			catch (Exception ex)
			{
				Log.LogError("[SceneExplorer] ME texture dedupe: シーンファイルは保存済みだがテクスチャの実データを書き込めなかった（このシーンのテクスチャは復元できません）: " + ex);
				return false;
			}
			finally
			{
				pendingBlob.Clear();
			}
		}

		/// <summary>WriteLocalCopy=false 用: メモリ上のシーン本体にチャンクを追記したストリームを返す。null = 失敗。
		/// bodyLength までを本体として使い（GetBuffer + Length でもよい）、ToArray のコピーを作らない。</summary>
		internal static MemoryStream AppendChunkToBuffer(byte[] body, int bodyLength, string path)
		{
			savingDepth = 0;
			try
			{
				if (body == null || bodyLength <= 0) return null;
				if (pendingBlob.Count == 0)
				{
					Log.LogInfo("[SceneExplorer] ME texture dedupe: 対象テクスチャ無し（チャンク不要 = バニラ）");
					MemoryStream plain = new MemoryStream(bodyLength);
					plain.Write(body, 0, bodyLength);
					return plain;
				}
				long total = bodyLength + ChunkTotalSize();
				if (total > int.MaxValue)
				{
					Log.LogError("[SceneExplorer] ME texture dedupe: シーン + チャンクが 2GiB を超えるため中止（このシーンのテクスチャは復元できません）: " + path);
					return null;
				}
				// 必要長を先に確保する（倍々成長の再確保を挟まない）
				MemoryStream ms = new MemoryStream((int)total);
				ms.Write(body, 0, bodyLength);
				if (!WriteChunk(ms, path)) return null;
				// 追記結果をメモリ上で検証（ファイル版と同じ検査）
				if (!VerifyChunkCore(new MemoryStream(ms.GetBuffer(), 0, (int)ms.Length, false), path)) return null;
				return ms;
			}
			catch (Exception ex)
			{
				Log.LogError("[SceneExplorer] ME texture dedupe: メモリ上のシーンへチャンクを追記できなかった（このシーンのテクスチャは復元できません）: " + ex);
				return null;
			}
			finally
			{
				pendingBlob.Clear();
			}
		}

		/// <summary>チャンク全体（ヘッダ + 全エントリ + フッタ）の長さ。</summary>
		private static long ChunkTotalSize()
		{
			long uniqueBytes = 0;
			foreach (KeyValuePair<long, byte[]> kv in pendingBlob)
			{
				uniqueBytes += kv.Value.Length;
			}
			return ChunkHeaderSize + ChunkFooterSize + uniqueBytes + (long)pendingBlob.Count * EntryHeaderSize;
		}

		/// <summary>チャンク本体（ヘッダ + 全エントリ + フッタ）を output の現在位置へ書く。</summary>
		private static bool WriteChunk(Stream output, string path)
		{
			long chunkSize = ChunkTotalSize();
			if (chunkSize > uint.MaxValue)
			{
				Log.LogError("[SceneExplorer] ME texture dedupe: チャンクが " + FormatBytes(chunkSize) + " で 4GiB を超えるため中止（このシーンのテクスチャは復元できません）: " + path);
				return false;
			}
			long uniqueBytes = chunkSize - ChunkHeaderSize - ChunkFooterSize - (long)pendingBlob.Count * EntryHeaderSize;

			long rawBytes = 0;
			long start = output.Position;
			BinaryWriter writer = new BinaryWriter(output);
			writer.Write(ChunkMagic);
			writer.Write((uint)pendingBlob.Count);
			foreach (KeyValuePair<long, byte[]> kv in pendingBlob)
			{
				writer.Write(kv.Key);
				writer.Write((uint)kv.Value.Length);
				writer.Write(kv.Value);
				rawBytes += kv.Value.Length;
			}
			writer.Flush();
			long payloadLength = output.Position - start;
			writer.Write((uint)payloadLength);
			writer.Write(ChunkMagic);
			writer.Flush();

			Log.LogInfo("[SceneExplorer] ME texture dedupe 追記: ユニーク " + pendingBlob.Count
				+ " 枚 / " + FormatBytes(uniqueBytes) + "（追記 " + FormatBytes(rawBytes + ChunkHeaderSize + ChunkFooterSize
				+ (long)pendingBlob.Count * EntryHeaderSize) + "）: " + path);
			return true;
		}

		/// <summary>追記後の検証。書き出した pendingBlob と、読み直したチャンクの枚数/hash 集合が一致するか。</summary>
		internal static bool VerifyChunk(string path)
		{
			using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			{
				return VerifyChunkCore(stream, path);
			}
		}

		private static bool VerifyChunkCore(Stream stream, string path)
		{
			long bodyLength;
			Dictionary<long, byte[]> verify = ReadChunk(stream, true, out bodyLength);
			if (verify == null)
			{
				Log.LogError("[SceneExplorer] ME texture dedupe: チャンク追記後の検証に失敗（チャンクを読めない）: " + path);
				return false;
			}
			if (verify.Count != pendingBlob.Count)
			{
				Log.LogError("[SceneExplorer] ME texture dedupe: チャンク検証で枚数不一致（" + verify.Count + " / " + pendingBlob.Count + "）: " + path);
				return false;
			}
			foreach (KeyValuePair<long, byte[]> kv in pendingBlob)
			{
				if (!verify.ContainsKey(kv.Key))
				{
					Log.LogError("[SceneExplorer] ME texture dedupe: チャンク検証で hash 欠落: hash=" + kv.Key + ": " + path);
					return false;
				}
			}
			return true;
		}

		private static object GetMarkerToken(long hash)
		{
			object token;
			if (markerTokens.TryGetValue(hash, out token)) return token;

			byte[] marker = new byte[MarkerSize];
			Buffer.BlockCopy(MarkerMagic, 0, marker, 0, MarkerMagic.Length);
			Buffer.BlockCopy(BitConverter.GetBytes(hash), 0, marker, MarkerMagic.Length, 8);

			// マーカー用の使い捨て TextureContainer から token だけ借りる（Texture は生成されない）
			object temp = containerCtor.Invoke(new object[] { marker });
			token = tokenField.GetValue(temp);
			markerTokens[hash] = token;
			return token;
		}

		#endregion

		#region ロード

		/// <summary>SceneInfo.Load prefix: チャンクを読んで共有表とシーン本体を用意する（チャンク無し = 素通り）。
		/// 本体はここで 1 回だけ読み、SceneInfo 本体の読み込みは MemoryReadStream からメモリで行う。</summary>
		internal static void BeginSceneLoad(string path)
		{
			if (string.IsNullOrEmpty(path)) return;
			// 毎回走る（同一パス 2 回目のためにガードを置かない）。同じパスを 2 回ロードしても
			// 冒頭の Invalidate で古いバッファを捨て、ディスクの現在の内容から作り直す。
			loadedBlob = null;
			loadedPath = path;
			if (!File.Exists(path)) return;

			// これからディスクから読み直すので、そのパスの古いキャッシュは先に捨てる
			// （チャンク無し/読取失敗のときに保存前の body を掴まないため。成功時のみ再キャッシュする）
			MemoryReadStream.Invalidate(path);

			try
			{
				using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				{
					long bodyLength;
					Dictionary<long, byte[]> table = ReadChunk(stream, false, out bodyLength);
					if (table == null) return;
					loadedBlob = table;
					// シーン本体（チャンク手前まで）もここで読む。開いたまま読むのでディスクは 1 回だけ
					byte[] body = ReadBody(stream, bodyLength);
					if (body != null) MemoryReadStream.Cache(path, body, body.Length);
					QueueConvert(table);
					Log.LogInfo("[SceneExplorer] ME texture dedupe チャンク検出: " + table.Count + " 枚を展開: " + path);
				}
			}
			catch (Exception ex)
			{
				loadedBlob = null;
				Log.LogWarning("[SceneExplorer] ME texture dedupe: チャンク読取失敗（バニラとして読み込みます）: " + ex.Message);
			}
		}

		/// <summary>シーン本体（チャンク手前まで）を読む。stream は先頭から読む前提。</summary>
		private static byte[] ReadBody(Stream stream, long length)
		{
			if (length <= 0 || length > int.MaxValue) return null;
			stream.Seek(0, SeekOrigin.Begin);
			byte[] body = new byte[(int)length];
			if (!ReadFull(stream, body, body.Length)) return null;
			return body;
		}

		/// <summary>LoadBundled postfix: マーカーを実データへ戻す。
		/// 共有表 loadedBlob はここでは捨てない。LoadBundled は 1 回のロードで複数回呼ばれる（辞書ごと）ため、
		/// 捨てるのは次の BeginSceneLoad（冒頭の loadedBlob = null）。</summary>
		internal static void RestoreLoadedBlob(object result)
		{
			IDictionary dict = result as IDictionary;
			if (dict == null)
			{
				if (result != null)
				{
					// 事前検査は void しか弾かない。辞書以外が返る形に変わっていたら黙らず知らせる
					Log.LogError("[SceneExplorer] ME texture dedupe: LoadBundled の戻り値が辞書でないためマーカーを展開できません: " + result.GetType().FullName);
				}
				return;
			}
			if (dict.Count == 0) return;

			int restored = 0;
			int missing = 0;
			bool reflectionFailed = false;
			foreach (DictionaryEntry entry in dict)
			{
				object container = entry.Value;
				if (container == null) continue;
				try
				{
					if (!EnsureReflection(container.GetType())) { reflectionFailed = true; continue; }
					byte[] data = (byte[])dataProp.GetValue(container, null);
					if (data == null || !IsMarker(data)) continue;

					// 共有表が無い（チャンク欠落・破損）のにマーカーが残っている = 復元不能を可視化する
					if (loadedBlob == null) { missing++; continue; }

					long hash = BitConverter.ToInt64(data, MarkerMagic.Length);
					byte[] real;
					if (!loadedBlob.TryGetValue(hash, out real)) { missing++; continue; }
					// Data setter は Acquire(実データ) → Release(マーカー) の順で正しく参照を付け替える
					dataProp.SetValue(container, real, null);
					restored++;
				}
				catch (Exception ex)
				{
					Log.LogWarning("[SceneExplorer] ME texture dedupe: 展開失敗（そのテクスチャはマーカーのまま）: " + ex.Message);
				}
			}

			if (missing > 0)
			{
				if (loadedBlob == null)
				{
					Log.LogError("[SceneExplorer] ME texture dedupe: 共有表が無いのにマーカーが " + missing + " 枚あります（このシーンのテクスチャは復元できません）: " + loadedPath);
				}
				else
				{
					Log.LogWarning("[SceneExplorer] ME texture dedupe: 共有表に無いマーカー " + missing + " 枚（そのテクスチャは復元できません）: " + loadedPath);
				}
			}
			if (restored > 0)
			{
				Log.LogInfo("[SceneExplorer] ME texture dedupe: " + restored + " 枚を共有表から復元: " + loadedPath);
			}
			if (reflectionFailed)
			{
				Log.LogWarning("[SceneExplorer] ME texture dedupe: TextureContainer 型を解決できず展開できませんでした: " + loadedPath);
			}
		}

		/// <summary>末尾の dedupe チャンクを読む。headersOnly = データ本体を読まず hash と長さだけ（検証用）。
		internal static Dictionary<long, byte[]> ReadChunk(string path, bool headersOnly)
		{
			using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			{
				long bodyLength;
				return ReadChunk(stream, headersOnly, out bodyLength);
			}
		}

		/// <summary>末尾の dedupe チャンクをストリームから読む。bodyLength = チャンク手前までの長さ（シーン本体）。</summary>
		internal static Dictionary<long, byte[]> ReadChunk(Stream stream, bool headersOnly, out long bodyLength)
		{
			bodyLength = -1;
			{
				long fileLength = stream.Length;
				if (fileLength < ChunkFooterSize + ChunkHeaderSize) return null;

				byte[] footer = new byte[ChunkFooterSize];
				stream.Seek(fileLength - ChunkFooterSize, SeekOrigin.Begin);
				if (!ReadFull(stream, footer, footer.Length)) return null;
				if (!MagicAt(footer, 4)) return null;   // magic が無ければバニラ

				long payloadLength = BitConverter.ToUInt32(footer, 0);
				long start = fileLength - ChunkFooterSize - payloadLength;
				if (start < 0) return null;

				stream.Seek(start, SeekOrigin.Begin);
				byte[] header = new byte[ChunkHeaderSize];
				if (!ReadFull(stream, header, header.Length)) return null;
				if (!MagicAt(header, 0)) return null;

				long count = BitConverter.ToUInt32(header, 8);
				// 最低 1 エントリ 12 バイト。count を payload 長で検証してから容量を取る
				if (count <= 0 || count * (long)EntryHeaderSize > payloadLength) return null;

				Dictionary<long, byte[]> table = new Dictionary<long, byte[]>((int)Math.Min(count, 65536));
				for (long i = 0; i < count; i++)
				{
					byte[] entry = new byte[EntryHeaderSize];
					if (!ReadFull(stream, entry, entry.Length)) return null;
					long hash = BitConverter.ToInt64(entry, 0);
					long dataLength = BitConverter.ToUInt32(entry, 8);
					if (dataLength > int.MaxValue) return null;
					long end = stream.Position + dataLength;
					if (end > start + payloadLength) return null;

					if (headersOnly)
					{
						table[hash] = null;
						stream.Seek(dataLength, SeekOrigin.Current);
						continue;
					}
					byte[] data = new byte[(int)dataLength];
					if (!ReadFull(stream, data, data.Length)) return null;
					table[hash] = data;
				}

				// フッタ直前でぴったり終わることまで確認する（破損チャンクは捨てる）
				if (stream.Position != start + payloadLength) return null;
				bodyLength = start;
				return table;
			}
		}

		private static bool ReadFull(Stream stream, byte[] buffer, int count)
		{
			int offset = 0;
			while (offset < count)
			{
				int read = stream.Read(buffer, offset, count - offset);
				if (read <= 0) return false;
				offset += read;
			}
			return true;
		}

		#endregion

		#region 共通

		private static bool IsMarker(byte[] data)
		{
			if (data == null || data.Length != MarkerSize) return false;
			return MagicAt(data, 0, MarkerMagic);
		}

		private static bool MagicAt(byte[] buffer, int offset)
		{
			return MagicAt(buffer, offset, ChunkMagic);
		}

		private static bool MagicAt(byte[] buffer, int offset, byte[] magic)
		{
			for (int i = 0; i < magic.Length; i++)
			{
				if (buffer[offset + i] != magic[i]) return false;
			}
			return true;
		}

		private static string FormatBytes(long value)
		{
			if (value >= 1073741824L) return (value / 1073741824d).ToString("0.00") + " GB";
			if (value >= 1048576L) return (value / 1048576d).ToString("0.0") + " MB";
			if (value >= 1024L) return (value / 1024d).ToString("0.0") + " KB";
			return value + " B";
		}

		private static bool EnsureReflection(Type type)
		{
			if (type == null) return false;
			if (containerType == type && tokenField != null) return true;

			dataProp = type.GetProperty("Data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			hashProp = type.GetProperty("Hash", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			tokenField = type.GetField("_token", BindingFlags.NonPublic | BindingFlags.Instance);
			containerCtor = type.GetConstructor(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
				null, new Type[] { typeof(byte[]) }, null);

			if (dataProp == null || hashProp == null || tokenField == null || containerCtor == null)
			{
				Log.LogWarning("[SceneExplorer] ME texture dedupe: TextureContainer の内部構造が想定外（dedupe 無効）: " + type.FullName);
				containerType = null;
				return false;
			}

			containerType = type;
			return true;
		}

		#endregion
	}
}
