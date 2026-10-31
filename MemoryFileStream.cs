using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace KK_SceneExplorer
{
	/// <summary>
	/// Studio.SceneInfo のディスク I/O をメモリへ差し替えるための FileStream 派生（書き込み側）。
	/// base は CreateFileW("NUL") のハンドルで開く（"NUL" は FileStream(string) では開けない。開けても Seek 不可）。
	/// Write/Seek/Length/Position/Flush は内部 MemoryStream に委譲し、base のハンドルは Dispose で閉じるためだけに持つ。
	/// net35: IntPtr.Add / FileStream.Flush(bool) / SafeFileHandle の自前構築は使わない。
	/// </summary>
	public sealed class MemoryFileStream : FileStream
	{
		private static ManualLogSource Log { get { return SceneExplorerPlugin.Log; } }

		/// <summary>直近に生成した書き込み用ストリーム。SceneInfo.Save の postfix がここから Buffer を取る
		/// （Save はシーン 1 つにつき 1 回しか走らない）。</summary>
		internal static MemoryFileStream LastCreated;

		private readonly MemoryStream inner = new MemoryStream();
		private readonly string path;
		private bool commitAttempted;
		private bool commitFailed;

#pragma warning disable 618 // net35 では FileStream(IntPtr, FileAccess) を使う（SafeFileHandle は自前構築しない）
		public MemoryFileStream(string path, FileMode mode, FileAccess access)
			: base(NulDevice.Open(), FileAccess.Write)
#pragma warning restore 618
		{
			this.path = path;
			LastCreated = this;
		}

		/// <summary>保存されたシーンのバイト列。</summary>
		internal MemoryStream Buffer { get { return inner; } }

		public override bool CanRead { get { return false; } }
		public override bool CanSeek { get { return true; } }
		public override bool CanWrite { get { return true; } }
		public override long Length { get { return inner.Length; } }
		public override long Position { get { return inner.Position; } set { inner.Position = value; } }
		public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
		public override int ReadByte() { throw new NotSupportedException(); }
		public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); }
		public override void WriteByte(byte value) { inner.WriteByte(value); }
		public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
		public override void SetLength(long value) { inner.SetLength(value); }
		public override void Flush() { inner.Flush(); }

		/// <summary>Dispose では書き出さない（ディスク I/O ゼロが目的）。ローカルコピーが要る経路だけ
		/// postfix が明示的に Commit() を呼ぶ（設定 [Save] WriteLocalCopy / 転送先が無い場合）。</summary>
		protected override void Dispose(bool disposing)
		{
			base.Dispose(disposing);
		}

		/// <summary>メモリ上のシーン（チャンク追記済み）を本来のパスへ書き出す。false = 失敗。
		/// 呼ぶのはローカルコピーが必要なときだけ（postfix から明示的に）。</summary>
		internal bool Commit()
		{
			if (commitAttempted) return !commitFailed;
			commitAttempted = true;
			try
			{
				using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write))
				{
					inner.WriteTo(output);
					output.Flush();
				}
				Log.LogInfo("[SceneExplorer] シーンをメモリから書き出し: " + inner.Length + " bytes / " + path);
				return true;
			}
			catch (Exception ex)
			{
				commitFailed = true;
				Log.LogError("[SceneExplorer] メモリ保存の書き出しに失敗（シーンファイルは作られていません）: " + path + " / " + ex);
				return false;
			}
		}

		/// <summary>直近の書き込みストリームを取り出して参照を切る（古いバッファを掴まないため）。</summary>
		internal static MemoryFileStream TakeLast()
		{
			MemoryFileStream stream = LastCreated;
			LastCreated = null;
			return stream;
		}
	}

	/// <summary>メモリ上のシーンバイト列を読むための FileStream 派生（SceneInfo.Load の差し替え用）。</summary>
	public sealed class MemoryReadStream : FileStream
	{
		// 直近に読み込んだ/保存したシーンのバイト列（パスが変われば置き換わるだけ）
		private static readonly object cacheLock = new object();
		private static string cachedPath;
		private static byte[] cachedData;

		private readonly MemoryStream inner;

#pragma warning disable 618 // base のハンドルは読み書きに使わないので、実測で通った書き込み用 NUL ハンドルを共通で使う
		public MemoryReadStream(byte[] data)
			: base(NulDevice.Open(), FileAccess.Write)
#pragma warning restore 618
		{
			inner = new MemoryStream(data, false);
		}

		/// <summary>シーン保存直後の完成バイト列（チャンク追記済み）を保持する。次の保存で置き換わるだけで、
		/// 読み出しでは消費しない（同じシーンを何度もロードしても毎回ディスクへ戻らない）。</summary>
		internal static void Cache(string path, byte[] data)
		{
			lock (cacheLock)
			{
				cachedPath = path;
				cachedData = data;
			}
		}

		/// <summary>保持しているシーンバイト列を取り出す（消費しない）。</summary>
		internal static bool TryGetCached(string path, out byte[] data)
		{
			lock (cacheLock)
			{
				if (cachedData != null && cachedPath != null && string.Equals(cachedPath, path, StringComparison.OrdinalIgnoreCase))
				{
					data = cachedData;
					return true;
				}
			}
			data = null;
			return false;
		}

		/// <summary>SceneInfo.Load の FileStream 生成を置換する。復元バッファがあればメモリから、無ければ本来のパスから開く。</summary>
		public static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
		{
			byte[] data;
			if (!TryGetCached(path, out data))
			{
				return new FileStream(path, mode, access, share);
			}
			SceneExplorerPlugin.Log.LogInfo("[SceneExplorer] シーン読み込みをメモリから実行: " + data.Length + " bytes / " + path);
			return new MemoryReadStream(data);
		}

		public override bool CanRead { get { return true; } }
		public override bool CanSeek { get { return true; } }
		public override bool CanWrite { get { return false; } }
		public override long Length { get { return inner.Length; } }
		public override long Position { get { return inner.Position; } set { inner.Position = value; } }
		public override int Read(byte[] buffer, int offset, int count) { return inner.Read(buffer, offset, count); }
		public override int ReadByte() { return inner.ReadByte(); }
		public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
		public override void WriteByte(byte value) { throw new NotSupportedException(); }
		public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
		public override void SetLength(long value) { throw new NotSupportedException(); }
		public override void Flush() { }
	}

	/// <summary>"NUL" デバイスのハンドル。FileStream の base 用（実際の読み書きには使わない）。</summary>
	internal static class NulDevice
	{
		[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr securityAttributes, uint disposition, uint flags, IntPtr template);

		/// <summary>GENERIC_WRITE のみ / FILE_SHARE_READ|WRITE / OPEN_EXISTING。FileStream(IntPtr) に渡せることは実測確認済み。</summary>
		internal static IntPtr Open()
		{
			IntPtr handle = CreateFileW("NUL", 0x40000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
			if (handle == new IntPtr(-1))
			{
				throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
			}
			return handle;
		}
	}
}
