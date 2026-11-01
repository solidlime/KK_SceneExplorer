using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Manager;
using Studio;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KK_SceneExplorer
{
	internal class FolderStatus
	{
		public string OriginalPath;
		public string EffectivePath;
		public bool Exists;
		public int FileCount;
		public string Error;
	}

	[BepInPlugin(SceneExplorerPlugin.GUID, "Scene Explorer", SceneExplorerPlugin.Version)]
	public class SceneExplorerPlugin : BaseUnityPlugin
	{
		public const string GUID = "KK_SceneExplorer";
		public const string Version = "3.0.15";

		public static Harmony HarmonyInstance;
		public static bool kkccDetected;
		private static float bfCheckTime;
		private static bool bfSearchDone;
		private float bfCheckStartTime;
		private static string lastNormalizeWarning;
		private static bool invalidPathWarned;

		public static ManualLogSource Log;
		public static ConfigFile ConfigFile;

		public static ConfigEntry<string> SceneFolders;
		public static ConfigEntry<string> CharaFolders;
		public static ConfigEntry<string> CoordinateFolders;
		public static ConfigEntry<bool> EnableCoordinateBrowser;
		// v3.4.1: キャラブラウザは一旦停止（メインゲームフック不具合のため。false でシーンブラウザのみ有効）
		public static ConfigEntry<bool> EnableCharaBrowser;
		public static ConfigEntry<KeyboardShortcut> SettingsKey;
		internal static ConfigEntry<int> FontSize;
		internal static ConfigEntry<int> BrowserWidth;
		internal static ConfigEntry<int> BrowserHeight;
		internal static ConfigEntry<int> ThumbSize;
		internal static ConfigEntry<float> TreeSplitPos;
		// v3.2.1: ファイルソート状態・最後に開いたシーンフォルダの記憶
		internal static ConfigEntry<int> SortMode;
		internal static ConfigEntry<bool> SortDescending;
		internal static ConfigEntry<string> LastFolder;
		/// <summary>ME テクスチャ dedupe の有効化（既定 OFF。ON にしたシーンはプラグイン無しで復元不可）</summary>
		public static ConfigEntry<bool> DedupeEnabled;
		// シーン保存時にローカルへファイルを作るか（既定 false = メモリ上のバイト列を最終保存先へ直接書く）
		public static ConfigEntry<bool> WriteLocalCopy;
		// 進捗トーストにメモリ使用量を出すか（既定 false）
		public static ConfigEntry<bool> ShowProgressMemory;
		// 保存開始からの経過時間（完了サマリ用）。転送スレッドと共有するので volatile にはせず参照だけ渡す
		internal static Stopwatch SaveWatch;

		internal static string lastSceneFolder;
		internal static string lastLoadedFolder;
		internal static string currentSceneFolder;
		internal static string currentLocalFolder;
		internal static bool applyingSelection;
		internal static Studio.SceneLoadScene activeLoadScene;

		/// <summary>ブラウザの操作対象モード（v3.1.0: キャラ/衣装対応）</summary>
		public enum BrowserMode { Scene, CharaFemale, CharaMale, Coordinate }

		// Awake した CharaList の一覧（女/男タブは別インスタンスの可能性があるため複数保持。Task2 の CharaListAwakePostfix で追記）
		internal static readonly List<Studio.CharaList> activeCharaLists = new List<Studio.CharaList>();
		internal static BrowserMode CurrentBrowserMode = BrowserMode.Scene;

		/// <summary>v3.2.0: モード対応のルートフォルダ一覧（複数登録対応）。存在するフォルダのみ返す。
		/// v3.3.1: ローカル（UserData 配下）を常に先頭に追加し、設定フォルダをその後ろに並べる
		/// （シーンモードの「ローカル + 設定フォルダ群」と同じ並びに統一）。設定がローカルと重複する場合は除外する。</summary>
		public static string[] GetModeRootFolders()
		{
			switch (CurrentBrowserMode)
			{
				case BrowserMode.CharaFemale:
				case BrowserMode.CharaMale:
				{
					string sub = (CurrentBrowserMode == BrowserMode.CharaFemale) ? "female" : "male";
					List<string> roots = new List<string>();
					// v3.3.1: ローカル（UserData\chara\sub）を先頭に追加（存在する場合のみ）
					string local = GetModeLocalRoot();
					if (local != null && Directory.Exists(local)) roots.Add(local);
					foreach (string baseDir in SplitFolderSettings(CharaFolders.Value))
					{
						string resolved = ResolveFolderSetting(baseDir + "\\" + sub);
						if (resolved != null && !SamePath(resolved, local)) roots.Add(resolved);
					}
					if (roots.Count == 0)
						Log.LogWarning("[SceneExplorer] キャラフォルダが見つかりません（CharaFolders 設定を確認してください）: " + CharaFolders.Value);
					return roots.ToArray();
				}
				case BrowserMode.Coordinate:
				{
					List<string> roots = new List<string>();
					// v3.3.1: ローカル（UserData\coordinate）を先頭に追加（存在する場合のみ）
					string local = GetModeLocalRoot();
					if (local != null && Directory.Exists(local)) roots.Add(local);
					foreach (string baseDir in SplitFolderSettings(CoordinateFolders.Value))
					{
						string resolved = ResolveFolderSetting(baseDir);
						if (resolved != null && !SamePath(resolved, local)) roots.Add(resolved);
					}
					if (roots.Count == 0)
						Log.LogWarning("[SceneExplorer] 衣装フォルダが見つかりません（CoordinateFolders 設定を確認してください）: " + CoordinateFolders.Value);
					return roots.ToArray();
				}
				default: return new string[0];
			}
		}

		/// <summary>v3.3.1: 現在のモードのローカルルート（UserData 配下）を返す。
		/// Directory.Exists チェックはしない（純粋にパスを返すのみ）。Scene モードは null。</summary>
		public static string GetModeLocalRoot()
		{
			switch (CurrentBrowserMode)
			{
				case BrowserMode.CharaFemale: return Path.Combine(Path.Combine(UserData.Path, "chara"), "female");
				case BrowserMode.CharaMale: return Path.Combine(Path.Combine(UserData.Path, "chara"), "male");
				case BrowserMode.Coordinate: return Path.Combine(UserData.Path, "coordinate");
				default: return null;
			}
		}

		/// <summary>v3.3.1: 2つのパスが同一フォルダかを判定する（大文字小文字を無視、末尾セパレータは無視）</summary>
		private static bool SamePath(string a, string b)
		{
			if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
			try
			{
				string na = Path.GetFullPath(a).TrimEnd('\\', '/');
				string nb = Path.GetFullPath(b).TrimEnd('\\', '/');
				return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
			}
			catch { return false; }
		}

		/// <summary>v3.2.0: フォルダ設定文字列（セミコロン区切り）を要素に分解する</summary>
		private static string[] SplitFolderSettings(string raw)
		{
			if (string.IsNullOrEmpty(raw)) return new string[0];
			string[] parts = raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
			List<string> result = new List<string>();
			foreach (string part in parts)
			{
				string trimmed = part.Trim();
				if (trimmed.Length > 0) result.Add(trimmed);
			}
			return result.ToArray();
		}

		/// <summary>v3.2.0: 設定フォルダを絶対パスに解決。絶対パスならそのまま、相対なら UserData.Path 配下。
		/// 存在しない場合は null（呼び出し側で除外する）。</summary>
		private static string ResolveFolderSetting(string path)
		{
			if (string.IsNullOrEmpty(path)) return null;
			path = path.Replace('/', '\\');
			string full = Path.IsPathRooted(path) ? path : Path.Combine(UserData.Path, path);
			return Directory.Exists(full) ? full : null;
		}

		/// <summary>キャラモード要求。CharaList が active になった時に AddButtonCtrl.OnClick Postfix から呼ばれる</summary>
		public static void RequestCharaMode(Studio.CharaList charaList)
		{
			if (charaList == null) return;
			int sex = 1;
			try { sex = (int)AccessTools.Field(typeof(Studio.CharaList), "sex").GetValue(charaList); }
			catch (Exception ex)
			{
				Log.LogWarning("CharaList.sex 読取失敗: " + ex.Message);
				RequestSceneMode("sex読取失敗");
				return;
			}
			CurrentBrowserMode = (sex == 1) ? BrowserMode.CharaFemale : BrowserMode.CharaMale;
			string[] roots = GetModeRootFolders();
			CurrentBrowserFolder = (roots.Length > 0) ? roots[0] : null;
			// 非表示は HideModePanels に一本化（スナップショット記録との整合性のため。ちらつき1フレームは許容）
			Log.LogInfo("[SceneExplorer] Charaモード: " + CurrentBrowserMode + " folder=" + CurrentBrowserFolder);
		}

		/// <summary>シーンモードへ戻す。タブ切替・Close・OnClickRoot 他タブから呼ばれる</summary>
		public static void RequestSceneMode(string reason)
		{
			if (CurrentBrowserMode != BrowserMode.Scene)
				Log.LogInfo("[SceneExplorer] モード解除(" + reason + "): " + CurrentBrowserMode + " -> Scene");
			CurrentBrowserMode = BrowserMode.Scene;
			CurrentBrowserFolder = null;
		}

		// ═══════════════════════════════════════════════════════════════
		// v3.4.0: メインゲーム（Koikatu）キャラロード対応
		// ═══════════════════════════════════════════════════════════════

		/// <summary>v3.4.0: メインゲーム（Koikatu / KoikatsuSunshine キャラエディタ）かどうか。
		/// Application.productName が "CharaStudio" 以外 = メインゲーム。</summary>
		public static bool IsMainGame
		{
			get
			{
				try { return !string.Equals(Application.productName, "CharaStudio", StringComparison.Ordinal); }
				catch { return false; }
			}
		}

		// キャラロードダイアログ（ChaCustom.CustomFileWindow）のリフレクションキャッシュ（v3.4.0）
		private static ChaCustom.CustomFileWindow _mainGameFileWindow;
		private static PropertyInfo _mainGameFwTypeProperty;
		private static Type _mainGameFwTypeEnumType;
		private static int _mainGameFwTypeCharaLoad = -1;
		private static int _mainGameFwTypeCharaSave = -1;
		private static FieldInfo _mainGameObjCharaLoadField;
		private static FieldInfo _mainGameBtnCloseField;
		private static FieldInfo _mainGameObjSaveField;
		private static bool _mainGameCharaLoadActive;
		private static bool _mainGameCharaSaveActive;

		/// <summary>v3.4.0: メインゲームでキャラ保存ダイアログ（CharaSave）を差し替え中かどうか。
		/// SceneBrowser のボトムバーを保存 UI（新規保存/上書き）に切り替える判定に使う。</summary>
		public static bool IsMainGameCharaSaveMode { get { return _mainGameCharaSaveActive; } }

		/// <summary>v3.4.0: メインゲーム用キャラモード起動。modeSex は KK 慣例（0=女 / 1=男）。
		/// スタジオ用 RequestCharaMode と同じ状態セットで、SceneBrowser 側のモード遷移検出で表示される。</summary>
		private static void RequestCharaModeMainGame(int modeSex)
		{
			CurrentBrowserMode = (modeSex == 0) ? BrowserMode.CharaFemale : BrowserMode.CharaMale;
			string[] roots = GetModeRootFolders();
			CurrentBrowserFolder = (roots.Length > 0) ? roots[0] : null;
			Log.LogInfo("[SceneExplorer] メインゲーム Charaモード: " + CurrentBrowserMode + " folder=" + CurrentBrowserFolder);
		}

		/// <summary>v3.4.0: メインゲームのキャラロードダイアログを閉じる（SceneBrowser の閉じるボタンから呼ばれる）。
		/// 1) 標準の閉じるボタン（btnClose.onClick.Invoke、ウィンドウ全体を非表示）→ 2) 発火できなかった場合は
		/// ウィンドウ全体を直接非表示 → 3) 標準パネル復元。
		/// fwType は書き換えない（書換えると保存モード側（CharaSave）の入遷移検知と相互に発火し、
		/// ロード⇔保存の無限ループになるため）。ウィンドウの非表示はポーリング側
		/// （DetectMainGameCharaLoad）の終了検知で検出され、ブラウザの終了処理が行われる。</summary>
		public static void CloseMainGameCharaLoad()
		{
			if (!IsMainGame) return;
			try
			{
				ChaCustom.CustomFileWindow window = ResolveMainGameFileWindow();
				if (window != null)
				{
					// 1) ゲーム標準の閉じる処理（btnClose.onClick → ウィンドウ全体を非表示）をそのまま実行
					InvokeMainGameCloseButton(window);
					// 2) btnClose が非アクティブ等で発火できなかった場合はウィンドウ全体を直接非表示
					if (window.gameObject != null && window.gameObject.activeSelf)
					{
						window.gameObject.SetActive(false);
					}
				}
				// 3) 標準パネル復元（最終状態を確実に成立させる）
				SetMainGameCharaPanelActive(true);
			}
			catch (Exception ex)
			{
				Log.LogWarning("[SceneExplorer] メインゲームキャラロードダイアログのクローズに失敗: " + ex.Message);
				try { SetMainGameCharaPanelActive(true); } catch { }
			}
		}

		/// <summary>v3.4.0: メインゲームのキャラロードダイアログ監視（Update から毎フレーム呼ばれる）。
		/// fwType が CharaLoad かつウィンドウ表示中に標準パネルを非表示にして SceneBrowser を表示し、
		/// ウィンドウが閉じられた（またはタブが切り替わった）瞬間に標準パネルを復元してブラウザを閉じる。
		/// 終了検知は fwType の変更ではなくウィンドウの activeSelf を基準にする（ゲームの閉じるボタンは
		/// fwType を変えずにウィンドウ全体を非表示にするため）。fwType への書き込みは一切行わない
		/// （書込むと保存モード側（DetectMainGameCharaSave）の入遷移と相互発火するため）。</summary>
		private static void DetectMainGameCharaLoad()
		{
			// v3.4.1: キャラブラウザ停止中（EnableCharaBrowser=false）は監視しない（標準 UI のまま）
			if (!EnableCharaBrowser.Value) return;
			try
			{
				ChaCustom.CustomFileWindow window = ResolveMainGameFileWindow();
				if (window == null) return;

				int fwType = ReadMainGameFwType(window);
				// 列挙値が解決できている場合のみ判定（-1 == -1 の誤発火防止）
				bool charaLoad = (_mainGameFwTypeCharaLoad >= 0) && (fwType == _mainGameFwTypeCharaLoad);
				bool windowVisible = (window.gameObject != null) && window.gameObject.activeSelf;

				if (_mainGameCharaLoadActive)
				{
					if (charaLoad && windowVisible)
					{
						// 表示中は毎フレーム非表示を維持（ゲーム側の再表示と競合しないよう）
						SetMainGameCharaPanelActive(false);
					}
					else
					{
						// キャラロード終了（ウィンドウが閉じられた or タブが切り替わった）: ブラウザを閉じる
						_mainGameCharaLoadActive = false;
						// ウィンドウ非表示での終了なら標準パネルを復元。タブ切替での終了は
						// ゲーム側の UpdateWindow がパネル状態を管理するため復元しない
						if (charaLoad) SetMainGameCharaPanelActive(true);
						if (CurrentBrowserMode != BrowserMode.Scene)
							RequestSceneMode("MainGameCharaLoadClose");
						Log.LogInfo("[SceneExplorer] メインゲームキャラロード終了: 標準UI復元");
					}
				}
				else if (charaLoad && windowVisible)
				{
					// キャラロード開始: 標準パネルを隠してブラウザを表示
					_mainGameCharaLoadActive = true;
					SetMainGameCharaPanelActive(false);
					int modeSex = 0;
					ChaCustom.CustomBase customBase = FindObjectOfType<ChaCustom.CustomBase>();
					if (customBase != null) modeSex = customBase.modeSex;
					RequestCharaModeMainGame(modeSex);
					Log.LogInfo("[SceneExplorer] メインゲームキャラロード開始: sex=" + modeSex);
				}
			}
			catch (Exception ex)
			{
				Log.LogWarning("[SceneExplorer] メインゲームキャラロード監視エラー: " + ex.Message);
			}
		}

		// ── リフレクションヘルパー（v3.4.0） ──

		/// <summary>CustomFileWindow を解決。Unity 破棄（シーン切替）時は自動で再取得する。</summary>
		private static ChaCustom.CustomFileWindow ResolveMainGameFileWindow()
		{
			if (_mainGameFileWindow == null)
			{
				_mainGameFileWindow = FindObjectOfType<ChaCustom.CustomFileWindow>();
				if (_mainGameFileWindow != null) EnsureMainGameFwTypeCache();
			}
			return _mainGameFileWindow;
		}

		/// <summary>fwType プロパティ・FileWindowType 列挙値・objCharaLoad/btnClose フィールドを初回に解決する。</summary>
		private static void EnsureMainGameFwTypeCache()
		{
			Type t = typeof(ChaCustom.CustomFileWindow);
			if (_mainGameFwTypeProperty == null)
			{
				_mainGameFwTypeProperty = t.GetProperty("fwType");
				if (_mainGameFwTypeProperty != null)
				{
					_mainGameFwTypeEnumType = _mainGameFwTypeProperty.PropertyType;
					try
					{
						_mainGameFwTypeCharaLoad = Convert.ToInt32(Enum.Parse(_mainGameFwTypeEnumType, "CharaLoad"));
						_mainGameFwTypeCharaSave = Convert.ToInt32(Enum.Parse(_mainGameFwTypeEnumType, "CharaSave"));
					}
					catch { }
					// Enum 名解決に失敗した場合のフォールバック（KK の FileWindowType: CharaLoad=1 / CharaSave=0）
					if (_mainGameFwTypeCharaLoad < 0) _mainGameFwTypeCharaLoad = 1;
					if (_mainGameFwTypeCharaSave < 0) _mainGameFwTypeCharaSave = 0;
				}
			}
			if (_mainGameObjCharaLoadField == null)
				_mainGameObjCharaLoadField = t.GetField("objCharaLoad", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (_mainGameObjSaveField == null)
				_mainGameObjSaveField = t.GetField("objSave", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (_mainGameBtnCloseField == null)
				_mainGameBtnCloseField = t.GetField("btnClose", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		}

		/// <summary>fwType を読む（FileWindowType の int 値。解決失敗時は -1）。</summary>
		private static int ReadMainGameFwType(ChaCustom.CustomFileWindow window)
		{
			try
			{
				EnsureMainGameFwTypeCache();
				if (_mainGameFwTypeProperty == null) return -1;
				return Convert.ToInt32(_mainGameFwTypeProperty.GetValue(window, null));
			}
			catch { return -1; }
		}

		/// <summary>標準のキャラロードパネル（objCharaLoad）を表示/非表示。変化時のみ SetActive。</summary>
		private static void SetMainGameCharaPanelActive(bool active)
		{
			try
			{
				ChaCustom.CustomFileWindow window = ResolveMainGameFileWindow();
				if (window == null) return;
				EnsureMainGameFwTypeCache();
				if (_mainGameObjCharaLoadField == null) return;
				GameObject panel = _mainGameObjCharaLoadField.GetValue(window) as GameObject;
				if (panel != null && panel.activeSelf != active) panel.SetActive(active);
			}
			catch { }
		}

		/// <summary>標準の閉じるボタン（btnClose）の onClick を直接発火する（ゲーム標準の閉じる処理）。</summary>
		private static void InvokeMainGameCloseButton(ChaCustom.CustomFileWindow window)
		{
			try
			{
				EnsureMainGameFwTypeCache();
				if (_mainGameBtnCloseField == null) return;
				object btnClose = _mainGameBtnCloseField.GetValue(window);
				if (btnClose == null) return;
				FieldInfo onClickField = btnClose.GetType().GetField("onClick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				UnityEngine.Events.UnityEvent evt = (onClickField != null) ? (onClickField.GetValue(btnClose) as UnityEngine.Events.UnityEvent) : null;
				if (evt != null) evt.Invoke();
			}
			catch (Exception ex)
			{
				Log.LogWarning("[SceneExplorer] btnClose.onClick の発火に失敗: " + ex.Message);
			}
		}

		/// <summary>v3.4.0: メインゲームのキャラ保存ダイアログを閉じる（SceneBrowser の閉じるボタンから呼ばれる）。
		/// ロード側（CloseMainGameCharaLoad）と同一方式: btnClose.onClick.Invoke → 失敗時はウィンドウ全体を
		/// 直接非表示 → 標準パネル復元。fwType は書き換えない。</summary>
		public static void CloseMainGameCharaSave()
		{
			if (!IsMainGame) return;
			try
			{
				ChaCustom.CustomFileWindow window = ResolveMainGameFileWindow();
				if (window != null)
				{
					InvokeMainGameCloseButton(window);
					if (window.gameObject != null && window.gameObject.activeSelf)
					{
						window.gameObject.SetActive(false);
					}
				}
				SetMainGameSavePanelActive(true);
			}
			catch (Exception ex)
			{
				Log.LogWarning("[SceneExplorer] メインゲームキャラ保存ダイアログのクローズに失敗: " + ex.Message);
				try { SetMainGameSavePanelActive(true); } catch { }
			}
		}

		/// <summary>v3.4.0: メインゲームのキャラ保存ダイアログ監視（Update から毎フレーム呼ばれる）。
		/// DetectMainGameCharaLoad と同型: fwType が CharaSave かつウィンドウ表示中に objSave を隠して
		/// SceneBrowser（保存 UI）を表示し、ウィンドウが閉じられた（またはタブが切り替わった）瞬間に
		/// 標準パネルを復元してブラウザを閉じる。ロード側と enum 上排他（CharaLoad/CharaSave は同時にならない）
		/// ため状態は独立して管理する。</summary>
		private static void DetectMainGameCharaSave()
		{
			// v3.4.1: キャラブラウザ停止中（EnableCharaBrowser=false）は監視しない（標準 UI のまま）
			if (!EnableCharaBrowser.Value) return;
			try
			{
				ChaCustom.CustomFileWindow window = ResolveMainGameFileWindow();
				if (window == null) return;

				int fwType = ReadMainGameFwType(window);
				// 列挙値が解決できている場合のみ判定（-1 == -1 の誤発火防止）
				bool charaSave = (_mainGameFwTypeCharaSave >= 0) && (fwType == _mainGameFwTypeCharaSave);
				bool windowVisible = (window.gameObject != null) && window.gameObject.activeSelf;

				if (_mainGameCharaSaveActive)
				{
					if (charaSave && windowVisible)
					{
						// 表示中は毎フレーム非表示を維持（ゲーム側の再表示と競合しないよう）
						SetMainGameSavePanelActive(false);
					}
					else
					{
						// キャラ保存終了（ウィンドウが閉じられた or タブが切り替わった）: ブラウザを閉じる
						_mainGameCharaSaveActive = false;
						// ウィンドウ非表示での終了なら標準パネルを復元。タブ切替での終了は
						// ゲーム側の UpdateWindow がパネル状態を管理するため復元しない
						if (charaSave) SetMainGameSavePanelActive(true);
						if (CurrentBrowserMode != BrowserMode.Scene)
							RequestSceneMode("MainGameCharaSaveClose");
						Log.LogInfo("[SceneExplorer] メインゲームキャラ保存終了: 標準UI復元");
					}
				}
				else if (charaSave && windowVisible)
				{
					// キャラ保存開始: 標準パネルを隠してブラウザ（保存 UI）を表示
					_mainGameCharaSaveActive = true;
					SetMainGameSavePanelActive(false);
					int modeSex = 0;
					ChaCustom.CustomBase customBase = FindObjectOfType<ChaCustom.CustomBase>();
					if (customBase != null) modeSex = customBase.modeSex;
					RequestCharaModeMainGame(modeSex);
					Log.LogInfo("[SceneExplorer] メインゲームキャラ保存開始: sex=" + modeSex);
				}
			}
			catch (Exception ex)
			{
				Log.LogWarning("[SceneExplorer] メインゲームキャラ保存監視エラー: " + ex.Message);
			}
		}

		/// <summary>v3.4.0: 標準のキャラ保存パネル（objSave）を表示/非表示。変化時のみ SetActive。</summary>
		private static void SetMainGameSavePanelActive(bool active)
		{
			try
			{
				ChaCustom.CustomFileWindow window = ResolveMainGameFileWindow();
				if (window == null) return;
				EnsureMainGameFwTypeCache();
				if (_mainGameObjSaveField == null) return;
				GameObject panel = _mainGameObjSaveField.GetValue(window) as GameObject;
				if (panel != null && panel.activeSelf != active) panel.SetActive(active);
			}
			catch { }
		}


		/// <summary>最後に押された追加タブ（0=女 / 1=男 / それ以外=未選択）。AddButtonCtrl.OnClick Postfix から記録。</summary>

		/// <summary>v2.1.1: 終了確認（StudioExit）・確認ダイアログ（StudioCheck）シーン表示中フラグ。</summary>
		internal static bool DialogSceneActive;

		// ── SceneBrowser用 static フィールド（v2.0.0） ──
		/// <summary>現在ブラウザで選択中のフォルダパス。null=ローカルルート。</summary>
		public static string CurrentBrowserFolder;

		/// <summary>ブラウザのベースパス（ローカルルート）を取得。</summary>
		internal static string GetBrowserBasePath()
		{
			try
			{
				return UserData.Create("studio/scene");
			}
			catch (Exception ex)
			{
				Log.LogWarning("ブラウザのベースパス取得に失敗: " + ex.Message);
				return "";
			}
		}

		/// <summary>SceneBrowser のスタイルを再生成させる（フォントサイズ変更の即時反映用）。</summary>
		public static void ResetBrowserStyles()
		{
			SceneBrowser browser = UnityEngine.Object.FindObjectOfType<SceneBrowser>();
			if (browser != null)
			{
				browser.RefreshStyles();
			}
		}

		private static bool hideLogged;
		private static bool hideErrorLogged;

		private void Awake()
		{
			Log = Logger;
			ConfigFile = Config;

			SceneFolders = Config.Bind("General", "SceneFolders", "",
				"セミコロン区切りのネットワークシーンフォルダ（例: Z:\\koikatsu_scenes;\\\\server\\share\\scenes）");
			SettingsKey = Config.Bind("General", "SettingsKey", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftControl),
				"設定ウィンドウの表示切替キー");
			FontSize = Config.Bind("UI", "FontSize", 14,
				new ConfigDescription("ブラウザのフォントサイズ（8〜32）", new AcceptableValueRange<int>(8, 32)));
			BrowserWidth = Config.Bind("UI", "BrowserWidth", 1280,
				new ConfigDescription("ブラウザの幅（800〜2560）", new AcceptableValueRange<int>(800, 2560)));
			BrowserHeight = Config.Bind("UI", "BrowserHeight", 800,
				new ConfigDescription("ブラウザの高さ（500〜1600）", new AcceptableValueRange<int>(500, 1600)));
			ThumbSize = Config.Bind("UI", "ThumbSize", 96,
				new ConfigDescription("サムネイルサイズ（48〜600）", new AcceptableValueRange<int>(48, 600)));
			TreeSplitPos = Config.Bind("UI", "TreeSplitPos", 240f, "ツリー/グリッド分割位置");
			EnableCoordinateBrowser = Config.Bind("General", "EnableCoordinateBrowser", false, "衣装ブラウザを使用する（v3.2.0 で一時停止中）");
			EnableCharaBrowser = Config.Bind("General", "EnableCharaBrowser", false, "キャラブラウザを使用する（v3.4.0 で一時停止中）");
			CharaFolders = Config.Bind("General", "CharaFolders", "", "キャラフォルダ（セミコロン区切り）。配下の female/male を女/男タブで自動参照");
			CoordinateFolders = Config.Bind("General", "CoordinateFolders", "", "衣装フォルダ（セミコロン区切り）");
			SortMode = Config.Bind("General", "SortMode", 1, "ファイルソート基準（0=名前, 1=日時, 2=サイズ）");
			SortDescending = Config.Bind("General", "SortDescending", true, "ファイルソート降順");
			LastFolder = Config.Bind("General", "LastFolder", "", "最後に開いたシーンフォルダ（空=ローカルルート）");
			// 実機で保存→ロード往復を確認済み（1.1GB → 209MB）。変更は次回起動から効く（Awake でパッチを張るため）
			DedupeEnabled = Config.Bind("Dedupe", "Enabled", true,
				"MaterialEditor テクスチャのシーン横断 dedupe を有効にする（変更は再起動後）");
			// false = 保存はメモリ上で完結し、最終保存先（NAS/ローカル）へ 1 回だけ書く。
			// 後続の mod がローカルファイルを読む場合や、書き出し失敗時にローカルへ落としたい場合は true
			WriteLocalCopy = Config.Bind("Save", "WriteLocalCopy", false,
				"シーン保存時にローカルへもファイルを作る（false=メモリから直接保存・KK_CardCompression 使用時は自動で true 相当）");
			ShowProgressMemory = Config.Bind("Save", "ShowProgressMemory", false,
				"進捗トーストにメモリ使用量（WorkingSet と GC ヒープ）を表示する（既定 false）");

			HarmonyInstance = new Harmony(GUID);
			Patches.ApplyAll(HarmonyInstance);
			DetectCardCompression();
			bfCheckStartTime = Time.realtimeSinceStartup;
			gameObject.AddComponent<SettingsUi>();
			gameObject.AddComponent<TransferNotice>();
			gameObject.AddComponent<SceneBrowser>();

			// v2.1.1: 終了確認（StudioExit）・確認ダイアログ（StudioCheck）シーンを監視。
			// 表示中は SceneBrowser を非表示にして、uGUI の確認ボタン（はい/いいえ）のクリックを奪わないようにする。
			try
			{
				UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnDialogSceneLoaded;
				UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnDialogSceneUnloaded;
			}
			catch (Exception ex)
			{
				Log.LogWarning("ダイアログシーン監視の登録に失敗: " + ex.Message);
			}

			Log.LogInfo("設定フォルダ: " + string.Join("; ", ScenePaths.GetConfiguredSceneFolders()));
			Log.LogInfo("Network Scene Folders loaded.");
		}

		private static void OnDialogSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
		{
			try
			{
				string n = scene.name;
				if (n == "StudioExit" || n == "StudioCheck")
				{
					DialogSceneActive = true;
				}
			}
			catch (Exception)
			{
			}
		}

		private static void OnDialogSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
		{
			try
			{
				string n = scene.name;
				if (n == "StudioExit" || n == "StudioCheck")
				{
					DialogSceneActive = false;
				}
			}
			catch (Exception)
			{
			}
		}

		private void DetectCardCompression()
		{
			try
			{
				Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
				foreach (Assembly assembly in assemblies)
				{
					if (assembly.GetName().Name == "KK_CardCompression")
					{
						kkccDetected = true;
						Log.LogInfo("KK_CardCompression検出: 圧縮完了待ち転送を有効化");
						break;
					}
				}
			}
			catch (Exception ex)
			{
				Log.LogWarning("KK_CardCompressionの検出に失敗しました: " + ex.Message);
			}
		}

		private void Update()
		{
			MeTextureDedupe.Tick(UnityEngine.Time.frameCount);
			if (IsMainGame)
			{
				// v3.4.0: メインゲーム（キャラエディタ）ではスタジオのシーン一覧UIが存在しないため、
				// キャラロード/保存ダイアログの監視に置き換える（無駄な FindObjectOfType 検索も排除）。
				// ロードと保存は enum 上排他なので両方監視してよい（状態は個別管理）
				DetectMainGameCharaLoad();
				DetectMainGameCharaSave();
			}
			else
			{
				ForceHideSceneLoadUi();
			}

			if (bfSearchDone)
			{
				return;
			}
			if (Time.realtimeSinceStartup - bfCheckTime < 0.5f)
			{
				return;
			}
			bfCheckTime = Time.realtimeSinceStartup;
			DetectBrowserFolders();
			if (Time.realtimeSinceStartup - bfCheckStartTime > 30f)
			{
				bfSearchDone = true;
				Log.LogInfo("BrowserFoldersが見つかりませんでした（独自ツリーモードで動作します）");
			}
		}

		private void ForceHideSceneLoadUi()
		{
			if (activeLoadScene == null)
			{
				activeLoadScene = FindObjectOfType<Studio.SceneLoadScene>();
			}
			if (activeLoadScene == null)
			{
				return;
			}
			try
			{
				GameObject root = activeLoadScene.transform.root.gameObject;
				if (root != null && root.activeSelf)
				{
					root.SetActive(false);
					if (!hideLogged)
					{
						hideLogged = true;
						Log.LogInfo("シーン一覧UIを非表示化（統合ブラウザ使用）");
					}
				}
			}
			catch (Exception ex)
			{
				if (!hideErrorLogged)
				{
					hideErrorLogged = true;
					Log.LogWarning("シーン一覧UIの非表示化に失敗: " + ex.Message);
				}
			}
		}

		private void DetectBrowserFolders()
		{
			string[] candidateNames = new string[]
			{
				"BrowserFolders.Hooks.KK.SceneFolders",
				"BrowserFolders.Hooks.KKS.SceneFolders",
				"BrowserFolders.Hooks.KKP.SceneFolders",
				"BrowserFolders.Studio.SceneFolders"
			};
			Type sceneType = null;
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			foreach (Assembly assembly in assemblies)
			{
				if (sceneType == null)
				{
					foreach (string name in candidateNames)
					{
						Type t = assembly.GetType(name, false);
						if (t != null)
						{
							sceneType = t;
							break;
						}
					}
				}
			}

			if (sceneType == null)
			{
				Log.LogWarning("BrowserFoldersの型解決に失敗しました（独自ツリーモードで動作します）");
				bfSearchDone = true;
				return;
			}

			BfInjection.SceneFoldersType = sceneType;
			BfInjection.Apply(HarmonyInstance);
			bfSearchDone = true;
			Log.LogInfo("BrowserFolders検出: BFのシーンツリーを無効化しました");
		}

		public static class ScenePaths
		{
			public static string[] GetConfiguredSceneFolders()
			{
				string raw = SceneFolders.Value;
				if (string.IsNullOrEmpty(raw))
				{
					return new string[0];
				}
				string[] parts = raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
				List<string> result = new List<string>();
				bool changed = false;
				foreach (string part in parts)
				{
					string trimmed = part.Trim();
					if (trimmed.Length > 0)
					{
						string normalized = trimmed.Replace('/', '\\');
						if (normalized.Length > 1 && normalized[0] == '\\' && normalized[1] != '\\')
						{
							normalized = "\\" + normalized;
						}
						if (normalized != trimmed)
						{
							string message = "パスを正規化しました: " + trimmed + " → " + normalized;
							if (message != lastNormalizeWarning)
							{
								lastNormalizeWarning = message;
								Log.LogWarning(message);
							}
							changed = true;
						}
						if (!IsValidRoot(normalized))
						{
							if (!invalidPathWarned)
							{
								invalidPathWarned = true;
								Log.LogWarning("設定されたネットワークフォルダのパスが不正です（再設定してください）: " + normalized);
							}
						}
						result.Add(normalized);
					}
				}
				if (changed)
				{
					// v3.3.1: バックスラッシュはエスケープせずそのまま保存（読込時に BepInEx がデコード、正規化で補正）
					SceneFolders.Value = string.Join(";", result.ToArray());
					ConfigFile.Save();
				}
				return result.ToArray();
			}

			private static bool IsValidRoot(string path)
			{
				if (string.IsNullOrEmpty(path))
				{
					return false;
				}
				if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\')
				{
					return true;
				}
				if (path.Length >= 2 && path[1] == ':')
				{
					return true;
				}
				return false;
			}

			public static string[] GetAllSceneFiles(string localDir, string pattern)
			{
				// 選択状態が生きている間は、applyingSelection の値に関わらず選択フォルダを維持する
				// （削除後も InitInfo が呼ばれ、applyingSelection=false のまま再構築されるため）
				if (SceneExplorerPlugin.applyingSelection
					|| !string.IsNullOrEmpty(SceneExplorerPlugin.currentSceneFolder)
					|| !string.IsNullOrEmpty(SceneExplorerPlugin.currentLocalFolder))
				{
					if (!string.IsNullOrEmpty(SceneExplorerPlugin.currentSceneFolder)
						&& Directory.Exists(SceneExplorerPlugin.currentSceneFolder))
					{
						SceneExplorerPlugin.lastSceneFolder = SceneExplorerPlugin.currentSceneFolder;
						List<string> files = new List<string>();
						AddFiles(files, SceneExplorerPlugin.currentSceneFolder, pattern);
						Log.LogInfo("スキャン: " + SceneExplorerPlugin.currentSceneFolder + " → " + files.Count + "件");
						return files.ToArray();
					}
					if (!string.IsNullOrEmpty(SceneExplorerPlugin.currentLocalFolder)
						&& Directory.Exists(SceneExplorerPlugin.currentLocalFolder))
					{
						SceneExplorerPlugin.lastSceneFolder = SceneExplorerPlugin.currentLocalFolder;
						List<string> files = new List<string>();
						AddFiles(files, SceneExplorerPlugin.currentLocalFolder, pattern);
						Log.LogInfo("スキャン: " + SceneExplorerPlugin.currentLocalFolder + " → " + files.Count + "件");
						return files.ToArray();
					}
				}

				// どちらも無効（例: NAS断線）または未選択ならローカルルート
				if (!string.IsNullOrEmpty(localDir))
				{
					SceneExplorerPlugin.lastSceneFolder = localDir;
				}

				List<string> fallbackFiles = new List<string>();
				AddFiles(fallbackFiles, localDir, pattern);
				Log.LogInfo("スキャン: " + localDir + " → " + fallbackFiles.Count + "件");
				return fallbackFiles.ToArray();
			}

			/// <summary>指定フォルダを直接スキャンする（SceneBrowser用）。ネットワーク/ローカル両対応。</summary>
			public static string[] ScanFolder(string folder, string pattern)
			{
				if (string.IsNullOrEmpty(folder))
				{
					return new string[0];
				}
				try
				{
					List<string> result = new List<string>(Directory.GetFiles(folder, pattern));
					SceneExplorerPlugin.lastSceneFolder = folder;
					return result.ToArray();
				}
				catch (Exception ex)
				{
					Log.LogWarning("フォルダのスキャンに失敗しました: " + folder + " - " + ex.Message);
					return new string[0];
				}
			}

			internal static FolderStatus[] GetSceneFolderStatuses()
			{
				string[] folders = GetConfiguredSceneFolders();
				FolderStatus[] statuses = new FolderStatus[folders.Length];
				for (int i = 0; i < folders.Length; i++)
				{
					statuses[i] = EvaluateFolder(folders[i]);
				}
				return statuses;
			}

			/// <summary>v3.2.0: 汎用フォルダステータス取得（キャラ/衣装設定用）。生のセミコロン区切り文字列から
			/// 分解し、各フォルダの存在と *.png ファイル数を評価する。</summary>
			internal static FolderStatus[] GetFolderStatuses(string rawFolders)
			{
				if (string.IsNullOrEmpty(rawFolders)) return new FolderStatus[0];
				string[] parts = rawFolders.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
				List<FolderStatus> statuses = new List<FolderStatus>();
				foreach (string part in parts)
				{
					string trimmed = part.Trim();
					if (trimmed.Length == 0) continue;
					statuses.Add(EvaluateGenericFolder(trimmed));
				}
				return statuses.ToArray();
			}

			private static FolderStatus EvaluateGenericFolder(string original)
			{
				FolderStatus status = new FolderStatus();
				status.OriginalPath = original;
				status.EffectivePath = null;
				status.Exists = false;
				status.FileCount = 0;
				status.Error = null;
				try
				{
					// v3.2.0: 実動作（ResolveFolderSetting）と同じ解決で評価。相対パスは UserData.Path 配下として扱う
					string checkPath = original.Replace('/', '\\');
					if (!Path.IsPathRooted(checkPath)) checkPath = Path.Combine(UserData.Path, checkPath);
					if (!Directory.Exists(checkPath))
					{
						status.Error = "フォルダが見つかりません";
						return status;
					}
					status.EffectivePath = checkPath;
					status.Exists = true;
					status.FileCount = Directory.GetFiles(checkPath, "*.png").Length;
				}
				catch (Exception ex)
				{
					status.Error = ex.Message;
				}
				return status;
			}

			private static FolderStatus EvaluateFolder(string original)
			{
				FolderStatus status = new FolderStatus();
				status.OriginalPath = original;
				status.Error = null;
				status.EffectivePath = null;
				status.Exists = false;
				status.FileCount = 0;

				string candidate1 = original;
				string candidate2 = original + "\\studio\\scene";
				int count1 = CountSceneFiles(candidate1);
				int count2 = CountSceneFiles(candidate2);

				string effective = null;
				if (count1 > 0)
				{
					effective = candidate1;
					status.FileCount = count1;
				}
				else if (count2 > 0)
				{
					effective = candidate2;
					status.FileCount = count2;
				}
				else if (count1 >= 0)
				{
					effective = candidate1;
					status.FileCount = count1;
				}
				else if (count2 >= 0)
				{
					effective = candidate2;
					status.FileCount = count2;
				}
				else
				{
					status.Error = "フォルダが見つかりません";
				}

				status.EffectivePath = effective;
				status.Exists = effective != null;

				if (effective != null)
				{
					if (effective == candidate2)
					{
						Log.LogInfo("パスを UserData\\studio\\scene 配下と解釈しました: " + original + " → " + effective);
					}
					Log.LogInfo("シーンフォルダ: " + original + " → " + effective + " (ファイル数 " + status.FileCount + ")");
				}
				return status;
			}

			private static int CountSceneFiles(string folder)
			{
				try
				{
					if (!Directory.Exists(folder))
					{
						return -1;
					}
					return Directory.GetFiles(folder, "*.png").Length;
				}
				catch (Exception ex)
				{
					Log.LogWarning("フォルダの確認に失敗しました: " + folder + " - " + ex.Message);
					return -1;
				}
			}

			private static void AddFiles(List<string> files, string folder, string pattern)
			{
				try
				{
					if (!Directory.Exists(folder))
					{
						return;
					}
					files.AddRange(Directory.GetFiles(folder, pattern));
				}
				catch (Exception ex)
				{
					Log.LogWarning("シーンフォルダの読み込みに失敗しました（スキップ）: " + folder + " - " + ex.Message);
				}
			}

			/// <summary>v2.5.4: Unity由来の非正規化パス（`/`区切り・`..`を含む）を `\`区切りの絶対パスに正規化。
			/// 例: "G:/MyGAME/Koikatsu/CharaStudio_Data/../UserData/studio/scene/Drew" → "G:\MyGAME\Koikatsu\UserData\studio\scene\Drew"。
			/// 失敗時（不正パス等）は元の文字列をそのまま返す。</summary>
			internal static string NormalizeFolderPath(string folder)
			{
				try
				{
					if (string.IsNullOrEmpty(folder))
					{
						return folder;
					}
					return Path.GetFullPath(folder);
				}
				catch
				{
					return folder;
				}
			}

			internal static bool IsNetworkSaveTarget()
			{
				return IsNetworkSaveTarget(SceneExplorerPlugin.lastSceneFolder);
			}

			internal static bool IsNetworkSaveTarget(string folder)
			{
				if (string.IsNullOrEmpty(folder))
				{
					return false;
				}
				folder = NormalizeFolderPath(folder);
				string[] roots = GetConfiguredSceneFolders();
				foreach (string root in roots)
				{
					string rootTrimmed = root.TrimEnd('\\');
					string rootWithSep = rootTrimmed + "\\";
					if (string.Equals(folder, rootTrimmed, StringComparison.OrdinalIgnoreCase)
						|| folder.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
				return false;
			}

			/// <summary>v2.5.2: 保存ファイルを「最後に開いたフォルダ」へ移動すべきかの判定。
			/// ネットワーク設定ルート配下、またはローカルルート（UserData/studio/scene）配下のサブフォルダなら true。
			/// ローカルルート自身は false（既にそこへ保存されるため移動不要）。</summary>
			internal static bool IsTransferTarget(string folder)
			{
				if (string.IsNullOrEmpty(folder))
				{
					return false;
				}
				folder = NormalizeFolderPath(folder);
				if (IsNetworkSaveTarget(folder))
				{
					return true;
				}
				// v2.5.5: 比較相手のローカルルートも正規化する（Unity由来の非正規化パスは
				// /区切り・CharaStudio_Data/../ を含み、正規化済み folder との StartsWith が不一致になるため）
				string localRoot = NormalizeFolderPath(UserData.Create("studio/scene")).TrimEnd('\\');
				if (string.Equals(folder, localRoot, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				return folder.StartsWith(localRoot + "\\", StringComparison.OrdinalIgnoreCase);
			}
		}

		public class Patches
		{
			public static void ApplyAll(Harmony harmony)
			{
				MethodInfo t1 = AccessTools.Method(typeof(Studio.SceneLoadScene), "Awake");
				if (t1 != null)
				{
					harmony.Patch(t1, postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(AwakePostfix))));
					Log.LogInfo("パッチ適用: Studio.SceneLoadScene.Awake");
				}
				else
				{
					Log.LogWarning("パッチ失敗: SceneLoadScene.Awake");
				}

				MethodInfo t2 = AccessTools.Method(typeof(Studio.SceneLoadScene), "InitInfo");
				if (t2 != null)
				{
					harmony.Patch(t2, transpiler: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(InitInfoTranspiler))));
					harmony.Patch(t2, postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(InitInfoPostfix))));
					Log.LogInfo("パッチ適用: Studio.SceneLoadScene.InitInfo");
				}
				else
				{
					Log.LogWarning("パッチ失敗: SceneLoadScene.InitInfo");
				}

				MethodInfo t3 = AccessTools.Method(typeof(Studio.SceneLoadScene), "OnClickClose");
				if (t3 != null)
				{
					harmony.Patch(t3, postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(OnClickClosePostfix))));
					Log.LogInfo("パッチ適用: Studio.SceneLoadScene.OnClickClose");
				}
				else
				{
					Log.LogWarning("パッチ失敗: SceneLoadScene.OnClickClose");
				}

				MethodInfo t5 = AccessTools.Method(typeof(Studio.Studio), "LoadSceneCoroutine");
				if (t5 != null)
				{
					harmony.Patch(t5, prefix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(LoadSceneCoroutinePrefix))));
					Log.LogInfo("パッチ適用: Studio.Studio.LoadSceneCoroutine");
				}
				else
				{
					Log.LogWarning("パッチ失敗: Studio.Studio.LoadSceneCoroutine");
				}

				MethodInfo t6 = AccessTools.Method(typeof(Studio.SceneInfo), "Save", new Type[] { typeof(string) });
				if (t6 != null)
				{
					harmony.Patch(t6,
						postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(SceneInfoSavePostfix))) { priority = 900 },
						finalizer: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(SceneInfoSaveFinalizer))));
					Log.LogInfo("パッチ適用: Studio.SceneInfo.Save");
				}
				else
				{
					Log.LogWarning("パッチ失敗: Studio.SceneInfo.Save");
				}
				// v3.1.0: キャラ/衣装ブラウザ
				MethodInfo t7 = AccessTools.Method(typeof(Studio.CharaList), "Awake");
				if (t7 != null)
				{
					harmony.Patch(t7, postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(CharaListAwakePostfix))));
					Log.LogInfo("パッチ適用: Studio.CharaList.Awake");
				}
				MethodInfo t8 = AccessTools.Method(typeof(Studio.AddButtonCtrl), "OnClick");
				if (t8 != null)
				{
					harmony.Patch(t8, postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(AddButtonOnClickPostfix))));
					Log.LogInfo("パッチ適用: Studio.AddButtonCtrl.OnClick");
				}
				MethodInfo t9 = AccessTools.Method(typeof(Studio.MPCharCtrl), "OnClickRoot");
				if (t9 != null)
				{
					harmony.Patch(t9, prefix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(MPCharCtrlOnClickRootPrefix))));
					Log.LogInfo("パッチ適用: Studio.MPCharCtrl.OnClickRoot");
				}

				if (SceneExplorerPlugin.DedupeEnabled.Value)
				{
					ApplyMeTextureDedupe(harmony);
					ApplyMemorySceneIo(harmony);
				}
				else
				{
					Log.LogInfo("ME texture dedupe: 設定で無効（既定 OFF）");
				}
			}

			/// <summary>シーン保存/読み込みの FileStream をメモリへ差し替える（dedupe 有効時のみ）。
			/// バニラ経路（対象外シーン）では書き出し内容・読み込み結果とも従来と同一になる。</summary>
			private static void ApplyMemorySceneIo(Harmony harmony)
			{
				try
				{
					MethodInfo sceneSave = AccessTools.Method(typeof(Studio.SceneInfo), "Save", new Type[] { typeof(string) });
					// Load(string) は Load(string, out Version) を呼ぶだけ。FileStream を開くのは後者
					MethodInfo sceneLoad = AccessTools.Method(typeof(Studio.SceneInfo), "Load",
						new Type[] { typeof(string), typeof(Version).MakeByRefType() });
					if (sceneSave == null || sceneLoad == null)
					{
						Log.LogWarning("メモリ I/O: SceneInfo.Save/Load が見つからず無効");
						return;
					}
					harmony.Patch(sceneSave, transpiler: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(SceneInfoSaveStreamTranspiler))));
					harmony.Patch(sceneLoad, transpiler: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(SceneInfoLoadStreamTranspiler))));
					// 共有表 loadedBlob の解放は SceneInfo.Load の postfix では行わない。LoadBundled（＝マーカー復元）は
					// SceneInfo.Load の後に走るので、ここで捨てるとテクスチャが復元できなくなる。
					// 解放は次の BeginSceneLoad（冒頭の loadedBlob = null）が担う。
					Log.LogInfo("パッチ適用: メモリ I/O (SceneInfo.Save / SceneInfo.Load の FileStream 差し替え)");
				}
				catch (Exception ex)
				{
					// ここで落ちても既存機能（ブラウザ/設定UI/転送/dedupe）は生き残らせる
					Log.LogWarning("メモリ I/O: パッチ適用に失敗したためメモリ I/O のみ無効化: " + ex);
				}
			}

			private static readonly Type[] SaveStreamSignature = new Type[] { typeof(string), typeof(FileMode), typeof(FileAccess) };
			private static readonly Type[] LoadStreamSignature = new Type[] { typeof(string), typeof(FileMode), typeof(FileAccess), typeof(FileShare) };

			/// <summary>SceneInfo.Save: newobj FileStream(string, FileMode, FileAccess) を MemoryFileStream に置換する（挿入しない）。</summary>
			private static IEnumerable<CodeInstruction> SceneInfoSaveStreamTranspiler(IEnumerable<CodeInstruction> instructions)
			{
				MethodBase target = AccessTools.Constructor(typeof(FileStream), SaveStreamSignature);
				MethodBase replacement = AccessTools.Constructor(typeof(MemoryFileStream), SaveStreamSignature);
				int replaced = 0;
				foreach (CodeInstruction instruction in instructions)
				{
					if (instruction.opcode == OpCodes.Newobj && SameMethod(instruction.operand as MethodBase, target))
					{
						instruction.operand = replacement;
						replaced++;
					}
					yield return instruction;
				}
				if (target == null || replacement == null)
				{
					SceneExplorerPlugin.Log.LogWarning("SceneInfo.Save Transpiler: FileStream/MemoryFileStream の ctor を解決できません");
				}
				else if (replaced == 0)
				{
					SceneExplorerPlugin.Log.LogWarning("SceneInfo.Save Transpiler: newobj FileStream(string,FileMode,FileAccess) が見つかりません（メモリ保存は無効のまま）");
				}
				else
				{
					SceneExplorerPlugin.Log.LogInfo("SceneInfo.Save Transpiler: FileStream を MemoryFileStream に置換しました（" + replaced + " 箇所）");
					if (replaced > 1)
					{
						// MemoryFileStream.LastCreated は最後の 1 個しか保持しない。最初のストリームの内容は転送されない
						SceneExplorerPlugin.Log.LogWarning("SceneInfo.Save Transpiler: FileStream 生成が " + replaced + " 箇所ありますが、メモリ保存で扱えるのは最後の 1 つだけです");
					}
				}
			}

			/// <summary>SceneInfo.Load: newobj FileStream(string, FileMode, FileAccess, FileShare) を
			/// MemoryReadStream.Open（保持しているシーンが無ければ本来のパスから開く）に置換する。</summary>
			private static IEnumerable<CodeInstruction> SceneInfoLoadStreamTranspiler(IEnumerable<CodeInstruction> instructions)
			{
				MethodBase target = AccessTools.Constructor(typeof(FileStream), LoadStreamSignature);
				MethodInfo replacement = AccessTools.Method(typeof(MemoryReadStream), nameof(MemoryReadStream.Open), LoadStreamSignature);
				int replaced = 0;
				foreach (CodeInstruction instruction in instructions)
				{
					if (instruction.opcode == OpCodes.Newobj && SameMethod(instruction.operand as MethodBase, target))
					{
						// 戻り値の型は FileStream のままなので、ローカルへの格納はバニラと同じ IL で成立する
						instruction.opcode = OpCodes.Call;
						instruction.operand = replacement;
						replaced++;
					}
					yield return instruction;
				}
				if (target == null || replacement == null)
				{
					SceneExplorerPlugin.Log.LogWarning("SceneInfo.Load Transpiler: FileStream の ctor / MemoryReadStream.Open を解決できません");
				}
				else if (replaced == 0)
				{
					SceneExplorerPlugin.Log.LogWarning("SceneInfo.Load Transpiler: newobj FileStream(string,FileMode,FileAccess,FileShare) が見つかりません（メモリ読み込みは無効のまま）");
				}
				else
				{
					SceneExplorerPlugin.Log.LogInfo("SceneInfo.Load Transpiler: FileStream を MemoryReadStream.Open に置換しました（" + replaced + " 箇所）");
				}
			}

			/// <summary>Harmony が渡す MethodBase と AccessTools の解決結果を突き合わせる（参照が別でも同一メソッドなら一致と見なす）。</summary>
			private static bool SameMethod(MethodBase a, MethodBase b)
			{
				if (a == null || b == null) return false;
				if (a == b) return true;
				try
				{
					return a.Module == b.Module && a.MetadataToken == b.MetadataToken;
				}
				catch (Exception)
				{
					return false;
				}
			}

			/// <summary>ME テクスチャのシーン横断 dedupe。ME 未導入・シグネチャ相違・パッチ失敗なら dedupe だけを無効化する。</summary>
			private static void ApplyMeTextureDedupe(Harmony harmony)
			{
				try
				{
					Type saveHandler = AccessTools.TypeByName("KK_Plugins.MaterialEditor.TextureSaveHandler");
					if (saveHandler == null)
					{
						Log.LogInfo("ME texture dedupe: MaterialEditor 未検出のため無効");
						return;
					}
					MethodInfo meSave = AccessTools.Method(saveHandler, "SaveBundled");
					MethodInfo meLoad = AccessTools.Method(saveHandler, "LoadBundled");
					if (meSave == null || meLoad == null)
					{
						Log.LogWarning("ME texture dedupe: TextureSaveHandler.SaveBundled/LoadBundled が見つからず無効");
						return;
					}

					// __2 / __result の位置バインドは相違しても無言 no-op になるため、先に明示検査する
					ParameterInfo[] saveParams = meSave.GetParameters();
					if (saveParams.Length < 3 || !IsDictionaryCompatible(saveParams[2].ParameterType))
					{
						Log.LogWarning("ME texture dedupe: SaveBundled の第3引数が辞書でない（想定外）ため無効");
						return;
					}
					if (meLoad.ReturnType == typeof(void))
					{
						Log.LogWarning("ME texture dedupe: LoadBundled が値を返さない（想定外）ため無効");
						return;
					}

					harmony.Patch(meSave,
						prefix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(MeTextureSavePrefix))),
						finalizer: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(MeTextureSaveFinalizer))));
					harmony.Patch(meLoad, postfix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(MeTextureLoadPostfix))));

					// チャンク追記は SceneInfoSavePostfix 内で転送キューの前に実行する（postfix の実行順に依存しない）
					MethodInfo sceneSave = AccessTools.Method(typeof(Studio.SceneInfo), "Save", new Type[] { typeof(string) });
					if (sceneSave != null)
					{
						harmony.Patch(sceneSave, prefix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(SceneInfoSaveDedupePrefix))));
					}

					// Load(string) は Load(string, out Version) を呼ぶだけ。両方フックしても BeginSceneLoad 側で冪等
					HarmonyMethod loadPrefix = new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(SceneInfoLoadPrefix)));
					MethodInfo loadSimple = AccessTools.Method(typeof(Studio.SceneInfo), "Load", new Type[] { typeof(string) });
					if (loadSimple != null) harmony.Patch(loadSimple, prefix: loadPrefix);
					MethodInfo loadVersion = AccessTools.Method(typeof(Studio.SceneInfo), "Load",
						new Type[] { typeof(string), typeof(Version).MakeByRefType() });
					if (loadVersion != null) harmony.Patch(loadVersion, prefix: loadPrefix);
					Func<byte[], int, int, byte[]> encoder = BuildWebPEncoder();
					MeTextureDedupe.SetWebPEncoder(encoder);
					Log.LogInfo("パッチ適用: ME texture dedupe (TextureSaveHandler / SceneInfo.Save / SceneInfo.Load)"
						+ (encoder == null ? " / WebP 変換なし（PNG 格納）" : " / WebP ロスレス変換あり（libwebp / 別スレッド）"));
				}
				catch (Exception ex)
				{
					// ここで落ちても既存機能（ブラウザ/設定UI/転送）は生き残らせる
					Log.LogWarning("ME texture dedupe: パッチ適用に失敗したため dedupe のみ無効化: " + ex);
				}
			}

			private static bool IsDictionaryCompatible(Type type)
			{
				return type == typeof(object) || typeof(System.Collections.IDictionary).IsAssignableFrom(type);
			}

			// 共有表に格納する PNG を WebP(ロスレス) に変換する。
			// Unity の Mono には GDI+ のアセンブリが無いので、PNG -> RGBA はメインスレッドで Texture2D が行う
			// （MeTextureDedupe.TryDecodePng）。ここはワーカースレッドから RGBA を受けて libwebp を叩くだけ。
			// DLL は build.cmd が BepInEx/plugins へコピーする（ME 同梱の libwebp.lib と同一バイナリ）。
			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate int WebPEncodeLosslessRGBAProc(byte[] rgba, int width, int height, int stride, out IntPtr output);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate void WebPFreeProc(IntPtr output);

			[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
			private delegate int WebPGetEncoderVersionProc();

			// デリゲートは呼び出し中 GC されないよう静的フィールドで保持する
			private static WebPEncodeLosslessRGBAProc webpEncodeLossless;
			private static WebPFreeProc webpFree;
			private static WebPGetEncoderVersionProc webpGetEncoderVersion;

			/// <summary>PNG バイト列をロスレス WebP に変換する。失敗時は null。</summary>
			internal static byte[] EncodeRgbaToWebP(byte[] rgba, int w, int h)
			{
				if (webpEncodeLossless == null || webpFree == null) return null;
				if (rgba == null || w <= 0 || h <= 0 || rgba.Length < w * h * 4) return null;

				IntPtr outPtr;
				int size = webpEncodeLossless(rgba, w, h, w * 4, out outPtr);
				if (size <= 0) return null;
				try
				{
					byte[] webp = new byte[size];
					Marshal.Copy(outPtr, webp, 0, size);
					return webp;
				}
				finally { webpFree(outPtr); }
			}

			[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
			private static extern IntPtr LoadLibraryW(string path);

			[DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
			private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

			/// <summary>モジュールから WebP の関数を解決する。1 つでも欠けたら false。</summary>
			private static bool ResolveWebPFunctions(IntPtr module)
			{
				IntPtr pEncode = GetProcAddress(module, "WebPEncodeLosslessRGBA");
				IntPtr pFree = GetProcAddress(module, "WebPFree");
				IntPtr pVersion = GetProcAddress(module, "WebPGetEncoderVersion");
				if (pEncode == IntPtr.Zero || pFree == IntPtr.Zero || pVersion == IntPtr.Zero)
				{
					Log.LogWarning("libwebp: エクスポートを解決できません (encode=" + pEncode + " free=" + pFree + " version=" + pVersion + ")");
					return false;
				}
				webpEncodeLossless = (WebPEncodeLosslessRGBAProc)Marshal.GetDelegateForFunctionPointer(pEncode, typeof(WebPEncodeLosslessRGBAProc));
				webpFree = (WebPFreeProc)Marshal.GetDelegateForFunctionPointer(pFree, typeof(WebPFreeProc));
				webpGetEncoderVersion = (WebPGetEncoderVersionProc)Marshal.GetDelegateForFunctionPointer(pVersion, typeof(WebPGetEncoderVersionProc));
				return true;
			}

			/// <summary>plugins 直下の libwebp.dll をフルパスで先に読み込む。Unity の検索パスには入っていない。</summary>
			private static bool EnsureWebPLoaded()
			{
				// 同梱先（plugins/KK_SceneExplorer/libwebp.dll）を最優先。
				// 次点は置き場所が変わった旧配置と、MaterialEditor 同梱の同一バイナリ
				string[] candidates = new string[]
				{
					Path.Combine(Path.Combine(Paths.PluginPath, "KK_SceneExplorer"), "libwebp.dll"),
					Path.Combine(Paths.PluginPath, "libwebp.dll"),
					Path.Combine(Path.Combine(Paths.PluginPath, "KK_Plugins"), "libwebp.lib"),
				};
				foreach (string path in candidates)
				{
					if (!File.Exists(path)) continue;
					try
					{
						IntPtr module = LoadLibraryW(path);
						if (module == IntPtr.Zero)
						{
							Log.LogWarning("LoadLibrary に失敗: " + path + " (err=" + Marshal.GetLastWin32Error() + ")");
							continue;
						}
						Log.LogInfo("libwebp を読み込んだ: " + path);
						if (!ResolveWebPFunctions(module)) continue;
						// 解決確認はバージョン取得で行う（WebPFree(IntPtr.Zero) は実機で例外になった）
						Log.LogInfo("libwebp エンコーダのバージョン: " + webpGetEncoderVersion());
						return true;
					}
					catch (Exception ex)
					{
						Log.LogWarning("libwebp 読み込み例外: " + path + " / " + ex.Message);
					}
				}
				return false;
			}

			private static Func<byte[], int, int, byte[]> BuildWebPEncoder()
			{
				if (!EnsureWebPLoaded())
				{
					Log.LogWarning("libwebp.dll が見つからないため WebP 変換を無効化（PNG で保存します）");
					return null;
				}
				return EncodeRgbaToWebP;
			}

			private static void SceneInfoSaveDedupePrefix()
			{
				MeTextureDedupe.BeginSceneSave(UnityEngine.Time.frameCount);
			}

			private static void SceneInfoLoadPrefix(string _path)
			{
				MeTextureDedupe.BeginSceneLoad(_path);
			}

			// Harmony の prefix は bool/void しか返せない。辞書そのものを書き換える。
			private static void MeTextureSavePrefix(object __2, out List<object[]> __state)
			{
				__state = MeTextureDedupe.SwapToMarkers(__2);
			}

			// 例外でも必ず走る。差し替えた token を元に戻す（savingDepth は EndSceneSave が戻す）。
			private static void MeTextureSaveFinalizer(List<object[]> __state)
			{
				MeTextureDedupe.RestoreMarkers(__state);
			}

			private static void MeTextureLoadPostfix(object __result)
			{
				MeTextureDedupe.RestoreLoadedBlob(__result);
			}

			private static IEnumerable<CodeInstruction> InitInfoTranspiler(IEnumerable<CodeInstruction> instructions)
			{
				MethodInfo replacement = AccessTools.Method(typeof(ScenePaths), nameof(ScenePaths.GetAllSceneFiles),
					new[] { typeof(string), typeof(string) });
				bool matched = false;
				foreach (CodeInstruction instruction in instructions)
				{
					if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo)
					{
						MethodInfo method = (MethodInfo)instruction.operand;
						if (method.Name == "GetFiles"
							&& method.DeclaringType == typeof(Directory)
							&& method.GetParameters().Length == 2
							&& method.GetParameters()[0].ParameterType == typeof(string)
							&& method.GetParameters()[1].ParameterType == typeof(string))
						{
							instruction.operand = replacement;
							matched = true;
						}
					}
					yield return instruction;
				}
				if (matched)
				{
					SceneExplorerPlugin.Log.LogInfo("InitInfo Transpiler: Directory.GetFiles(string,string) を置換しました");
				}
				else
				{
					SceneExplorerPlugin.Log.LogWarning("InitInfo Transpiler: 対象メソッドが見つかりません（パッチが効いていません）");
				}
			}

			private static void AwakePostfix(Studio.SceneLoadScene __instance)
			{
				SceneExplorerPlugin.activeLoadScene = __instance;
				SceneExplorerPlugin.currentSceneFolder = null;
				SceneExplorerPlugin.currentLocalFolder = null;
				SceneExplorerPlugin.applyingSelection = false;

				try
				{
				GameObject root = __instance.transform.root.gameObject;
				if (root != null && root.activeSelf)
					{
						root.SetActive(false);
						if (!hideLogged)
						{
							hideLogged = true;
							Log.LogInfo("シーン一覧UIを非表示化（統合ブラウザ使用）");
						}
					}
				}
				catch (Exception)
				{
				}
			}

			private static void InitInfoPostfix(Studio.SceneLoadScene __instance)
			{
				SceneExplorerPlugin.activeLoadScene = __instance;
			}

			private static void OnClickClosePostfix()
			{
				SceneExplorerPlugin.activeLoadScene = null;
				SceneExplorerPlugin.currentSceneFolder = null;
				SceneExplorerPlugin.currentLocalFolder = null;
				SceneExplorerPlugin.applyingSelection = false;
			}

			private static void LoadSceneCoroutinePrefix(string _path)
			{
				try
				{
					if (!string.IsNullOrEmpty(_path))
					{
						SceneExplorerPlugin.lastSceneFolder = Path.GetDirectoryName(_path);
						// v2.5.3: 読み込みフォルダは「上書きされない専用フィールド」にも記録（保存先の最優先）。
						// RescanFiles/GetAllSceneFiles が lastSceneFolder をローカルルートへ上書きしても失われない。
						SceneExplorerPlugin.lastLoadedFolder = Path.GetDirectoryName(_path);
					}
				}
				catch (Exception ex)
				{
					Log.LogWarning("読み込みフォルダの記録に失敗: " + ex.Message);
				}
			}

			private static void SceneInfoSavePostfix(Studio.SceneInfo __instance, string _path)
			{
				if (string.IsNullOrEmpty(_path))
				{
					return;
				}
				// メモリ保存（MemoryFileStream）の内容を扱う。既定は実ファイルを作らず、メモリ上のバイト列を
				// チャンク追記してそのまま最終保存先へ渡す（ディスク I/O は最終保存先への 1 回だけ）。
				MemoryFileStream saved = MemoryFileStream.TakeLast();
				// ToArray しない（GetBuffer + Length で直接渡す。200MB 級のシーンでコピーを作らない）
				byte[] scene = saved == null ? null : saved.Buffer.GetBuffer();
				int sceneLength = saved == null ? 0 : (int)saved.Buffer.Length;
				// ローカルにファイルを作る経路: 設定で明示された場合と、保存後に実ファイルを圧縮する KKCC が導入されている場合。
				// KKCC は本プラグインより後にロードされうるので、起動時の判定結果だけに頼らず毎回見る
				bool writeLocal = SceneExplorerPlugin.WriteLocalCopy.Value || SceneExplorerPlugin.kkccDetected
					|| Type.GetType("KK_CardCompression.Plugin, KK_CardCompression") != null;
				if (saved != null)
				{
					if (sceneLength == 0)
					{
						MeTextureDedupe.AbortSceneSave();
						TransferNotice.Show("シーンを保存できませんでした（メモリ上のデータが空）。\n" + Path.GetFileName(_path));
						return;
					}
					if (writeLocal && !saved.Commit())
					{
						MeTextureDedupe.AbortSceneSave();
						TransferNotice.Show("シーンを保存できませんでした（書き出しに失敗）。\n" + Path.GetFileName(_path));
						return;
					}
				}
				byte[] transferData = null;
				int transferLength = 0;
				if (saved == null || writeLocal)
				{
					// 実ファイル経路（transpiler 未適用 / WriteLocalCopy / KKCC）。転送先のサイズ一致判定で
					// ローカル原本が消えるとテクスチャが全損するので、DedupeEnabled で短絡してはいけない。
					// パッチ登録は Awake 時に確定するので、ON→OFF に切り替えると SaveBundled がマーカーを書いたまま
					// EndSceneSave が走らず、通知なしにテクスチャが全損する（始めたら必ず終える）。
					if (!MeTextureDedupe.EndSceneSave(_path))
					{
						TransferNotice.Show("このシーンのテクスチャは復元できません。再保存してください。\n" + Path.GetFileName(_path));
						return;
					}
				}
				else
				{
					// メモリ経路: 実ファイルを作らず、チャンク追記済みの完全なバイト列を転送層へ直渡しする
					MemoryStream complete = MeTextureDedupe.AppendChunkToBuffer(scene, sceneLength, _path);
					if (complete == null)
					{
						TransferNotice.Show("このシーンのテクスチャは復元できません。再保存してください。\n" + Path.GetFileName(_path));
						return;
					}
					transferData = complete.GetBuffer();
					transferLength = (int)complete.Length;
					// 直後の Load がディスクを触らずに済むよう保持する（次の保存/読み込みで置き換わるだけ）
					MemoryReadStream.Cache(_path, transferData, transferLength);
				}
				// v2.5.3: 転送先の優先順は「直近で読み込んだシーンのフォルダ」→「SceneBrowser で最後に開いたフォルダ」→「最後にスキャンしたフォルダ」。
				// シーン読み込み（LoadSceneCoroutine）で記録された読み込みフォルダを最優先にすることで、
				// RescanFiles/GetAllSceneFiles による lastSceneFolder のローカルルート上書きの影響を受けず、
				// 読み込んだフォルダへの保存が確実に守られる。
				string targetDir = SceneExplorerPlugin.lastLoadedFolder;
				if (string.IsNullOrEmpty(targetDir))
				{
					targetDir = SceneExplorerPlugin.CurrentBrowserFolder;
				}
				if (string.IsNullOrEmpty(targetDir))
				{
					targetDir = SceneExplorerPlugin.lastSceneFolder;
				}
				if (transferData != null)
				{
					// メモリ経路: 転送対象フォルダが無ければ保存先自体が最終保存先（ローカル保存）
					string finalPath = (!string.IsNullOrEmpty(targetDir) && ScenePaths.IsTransferTarget(targetDir))
						? Path.Combine(targetDir, Path.GetFileName(_path))
						: _path;
					Log.LogInfo("保存: メモリ上のデータを最終保存先へ書き出します: " + finalPath);
					byte[] data = transferData;
					int length = transferLength;
					SceneExplorerPlugin.SaveWatch = Stopwatch.StartNew();
					ThreadPool.QueueUserWorkItem(delegate { TransferBytes(data, length, finalPath); });
					return;
				}
				if (string.IsNullOrEmpty(targetDir))
				{
					return;
				}
				if (!ScenePaths.IsTransferTarget(targetDir))
				{
					// v2.5.3: 転送対象外スキップもログに残す（保存先の挙動確認用）
					Log.LogInfo("保存先は転送対象外（ローカルに残します）: " + targetDir);
					return;
				}
				string dest = Path.Combine(targetDir, Path.GetFileName(_path));
				Log.LogInfo("保存転送: " + _path + " → " + dest);
				if (SceneExplorerPlugin.kkccDetected)
				{
					ThreadPool.QueueUserWorkItem(delegate { DelayedTransfer(_path, dest); });
				}
				else
				{
					// 同期転送すると転送 + FlushFileBuffers 待ちで数十秒〜数分 Studio が固まるため、プールへ逃がす
					ThreadPool.QueueUserWorkItem(delegate { TransferNow(_path, dest); });
				}
			}

			/// <summary>v3.4.2: 保存失敗の検知。SaveScene の prefix を廃止したため、その try/catch の代わりに
			/// SceneInfo.Save の finalizer で例外を拾う（postfix は例外時に実行されない）。
			/// __exception は設定しない＝例外はそのまま伝播させる。</summary>
			private static void SceneInfoSaveFinalizer(string _path, Exception __exception)
			{
				// 中断された保存のバッファは捨てる（postfix が飛んでも次回以降が古いバイト列を掴まないように）
				MemoryFileStream.TakeLast();
				if (__exception == null)
				{
					return;
				}
				Log.LogError("シーンの保存に失敗しました: " + _path + " | " + __exception);
				// dedupe のゲートを戻す（postfix が飛んでも次回のカード保存を巻き込まない）
				MeTextureDedupe.AbortSceneSave();
			}

			// v3.1.0: CharaList のインスタンスを保持（Awake はスタジオ起動時に一度だけ呼ばれる。女/男タブは別インスタンスのためリストで保持）
			private static void CharaListAwakePostfix(Studio.CharaList __instance)
			{
				if (__instance == null) return;
				if (!SceneExplorerPlugin.activeCharaLists.Contains(__instance))
					SceneExplorerPlugin.activeCharaLists.Add(__instance);
			}

			// v3.1.0: タブ切替後、CharaList が表示状態になったらキャラモード開始（排他制御なので他タブなら自動で非表示になる）
			private static void AddButtonOnClickPostfix()
			{
				// v3.4.1: キャラブラウザ停止中（EnableCharaBrowser=false）は標準 UI のまま
				if (!SceneExplorerPlugin.EnableCharaBrowser.Value) return;
				Studio.CharaList activeList = null;
				foreach (var list in SceneExplorerPlugin.activeCharaLists)
				{
					if (list != null && list.gameObject.activeInHierarchy) { activeList = list; break; }
				}
				if (activeList != null)
				{
					SceneExplorerPlugin.RequestCharaMode(activeList);
				}
				else if (SceneExplorerPlugin.CurrentBrowserMode != BrowserMode.Scene)
				{
					SceneExplorerPlugin.RequestSceneMode("タブ切替");
				}
			}

			// v3.1.0: コスチュームタブ(_idx==4)で衣装モード開始、それ以外のタブ/閉じ(-1)で解除
			// _idx==4 の開始時のみ activeInHierarchy でガード（Awake 直後の初期化 OnClickRoot(select=-1) は
			// CurrentBrowserMode が Scene の間に発火するため else 分岐に入らず無害。パネル非表示時の
			// OnClickRoot(-1) による解除は許可する = ガードは緩めてある）
			// さらに、タブクリック以外にもキャラ選択変更（ociChar setter → UpdateInfo → OnClickRoot(select)）
			// で発火するため、既に Coordinate モード中の再発火（select==4 のまま別キャラ選択）では開始しない。
			private static void MPCharCtrlOnClickRootPrefix(Studio.MPCharCtrl __instance, int _idx)
			{
				if (__instance == null) return;
				if (_idx == 4)
				{
					// v3.2.0: 衣装ブラウザは一時停止中（false なら標準の衣装リスト動作に戻す）
					if (!EnableCoordinateBrowser.Value) return;
					if (!__instance.gameObject.activeInHierarchy) return;
					if (SceneExplorerPlugin.CurrentBrowserMode == BrowserMode.Coordinate) return;   // キャラ切替での誤再発火ガード
					SceneExplorerPlugin.CurrentBrowserMode = BrowserMode.Coordinate;
					// v3.2.0: 設定ルート（CoordinateFolders）を参照（キャラモードの RequestCharaMode と同型）
					string[] roots = SceneExplorerPlugin.GetModeRootFolders();
					SceneExplorerPlugin.CurrentBrowserFolder = (roots.Length > 0) ? roots[0] : null;
					HideCostumeRoot(__instance);   // 1フレームのちらつき防止のため prefix 内で直接非表示
					SceneExplorerPlugin.Log.LogInfo("[SceneExplorer] Coordinateモード開始 folder=" + SceneExplorerPlugin.CurrentBrowserFolder);
				}
				else if (SceneExplorerPlugin.CurrentBrowserMode == BrowserMode.Coordinate)
				{
					SceneExplorerPlugin.RequestSceneMode("コスチュームタブ切替");
				}
			}

			// v3.1.0: costumeInfo のルート（objRoot/root）を非表示にする（Update 側の毎フレーム解決を避けるため prefix で実行）
			private static void HideCostumeRoot(Studio.MPCharCtrl mp)
			{
				try
				{
					var fi = AccessTools.Field(typeof(Studio.MPCharCtrl), "costumeInfo");
					if (fi == null) return;
					var ci = fi.GetValue(mp);
					if (ci == null) return;
					var rootFi = AccessTools.Field(ci.GetType(), "objRoot") ?? AccessTools.Field(ci.GetType(), "root");
					if (rootFi == null) return;
					var go = rootFi.GetValue(ci) as GameObject;
					if (go != null && go.activeInHierarchy) go.SetActive(false);
				}
				catch (Exception ex) { Log.LogWarning("[SceneExplorer] コスチュームUI非表示失敗: " + ex.Message); }
			}

			// KK_CardCompression の圧縮完了待ちポーリング（500ms × 3600 = 30分）
			private const int TransferWaitAttempts = 3600;
			private const int TransferWaitIntervalMs = 500;
			// 変化検知後の安定確認（1秒間隔・最大30秒）
			private const int TransferStabilizeChecks = 2;
			private const int TransferStabilizeIntervalMs = 1000;
			private const int TransferStabilizeMaxAttempts = 30;
			// 転送リトライ（2秒間隔 × 5回）
			private const int TransferRetryAttempts = 5;
			private const int TransferRetryIntervalMs = 2000;

			[DllImport("kernel32.dll", SetLastError = true)]
			private static extern bool FlushFileBuffers(IntPtr hFile);

			private static void DelayedTransfer(string src, string dest)
			{
				try
				{
					// KK_CardCompression 未ロード、または圧縮設定が無効（Enable=false / Studio Scene=false）なら
					// 圧縮が走らないため、30分の待機をせず即転送する
					var kkccType = Type.GetType("KK_CardCompression.Plugin, KK_CardCompression");
					bool kkccCompressionActive = false;
					if (kkccType != null)
					{
						try
						{
							// KKCC の Enable / EnableOnStudioSceneSaving は public static プロパティ（ConfigEntry<bool>）
							var enable = (ConfigEntry<bool>)kkccType.GetProperty("Enable", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
							var studioScene = (ConfigEntry<bool>)kkccType.GetProperty("EnableOnStudioSceneSaving", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
							kkccCompressionActive = enable != null && studioScene != null && enable.Value && studioScene.Value;
						}
						catch (Exception ex)
						{
							Log.LogWarning("KK_CardCompressionの設定確認に失敗したため即転送します: " + ex.Message);
						}
					}
					if (!kkccCompressionActive)
					{
						// このメソッド自体がプール上で走るのでそのまま呼ぶ（再キューすると無意味な遅延になる）
						TransferNow(src, dest);
						return;
					}
					long firstSize = -1;
					DateTime firstWriteTime = DateTime.MinValue;
					for (int i = 0; i < TransferWaitAttempts; i++)
					{
						FileInfo fi = new FileInfo(src);
						if (!fi.Exists)
						{
							Log.LogWarning("転送対象がありません（保存が中断された可能性）: " + src);
							return;
						}
						long size = fi.Length;
						DateTime writeTime = fi.LastWriteTime;
						if (firstSize < 0)
						{
							firstSize = size;
							firstWriteTime = writeTime;
						}
						else if (size != firstSize || writeTime != firstWriteTime)
						{
							// 変化検知（サイズ or LastWriteTime）: KKCC の上書きスワップの瞬間を読まないよう安定確認してから転送
							if (WaitStable(src))
							{
								TransferNow(src, dest);
							}
							return;
						}
						Thread.Sleep(TransferWaitIntervalMs);
					}
					Log.LogWarning("KK_CardCompressionの圧縮完了を待てませんでした（未圧縮のまま転送します）: " + src);
					TransferNow(src, dest);
				}
				catch (Exception ex)
				{
					Log.LogError("ネットワーク転送（遅延）に失敗しました（ローカルに残します）: " + ex.ToString());
					TransferNotice.Show("NAS へ転送できませんでした。ローカルに残しています。\n" + Path.GetFileName(src));
				}
			}

			/// <summary>変化検知後: サイズと LastWriteTime が2回連続で不変になるまで待つ（1秒間隔・最大30秒）。
			/// ファイル消失時は false を返す（転送しない）。最大待ち後も安定しなければ true で転送する（タイムアウトのフォールバックと同じ趣旨）。</summary>
			private static bool WaitStable(string src)
			{
				long size = -1;
				DateTime writeTime = DateTime.MinValue;
				int stableCount = 0;
				for (int i = 0; i < TransferStabilizeMaxAttempts; i++)
				{
					Thread.Sleep(TransferStabilizeIntervalMs);
					FileInfo fi = new FileInfo(src);
					if (!fi.Exists)
					{
						Log.LogWarning("転送対象がありません（保存が中断された可能性）: " + src);
						return false;
					}
					if (size < 0)
					{
						size = fi.Length;
						writeTime = fi.LastWriteTime;
					}
					else if (fi.Length == size && fi.LastWriteTime == writeTime)
					{
						stableCount++;
						if (stableCount >= TransferStabilizeChecks)
						{
							return true;
						}
					}
					else
					{
						stableCount = 0;
						size = fi.Length;
						writeTime = fi.LastWriteTime;
					}
				}
				return true;
			}

			// 同一 src/dest への並行転送を直列化する。同時に走ると片方が他方の公開物を削除して全損しうる
			private static readonly object TransferGate = new object();

			private static void TransferNow(string src, string dest)
			{
				lock (TransferGate) { TransferNowCore(src, dest); }
			}

			/// <summary>メモリ上のシーンを最終保存先へ 1 回だけ書く（ローカルにステージングしない）。
			/// .part → FlushFileBuffers → サイズ一致 → 同一ディレクトリ内 Move の順はファイル版と同じ。
			/// 失敗時はメモリ上のデータしか無いため、ファイル版と違ってローカル原本は残らない（通知で明示）。</summary>
			private static void TransferBytes(byte[] data, int length, string dest)
			{
				lock (TransferGate) { TransferBytesCore(data, length, dest); }
			}

			// 進捗を出す下限。これ未満は一括で書く（1MB 刻みの通知が無駄になるだけ）
			private const int ProgressMinBytes = 4 << 20;
			private const int WriteChunkSize = 1 << 20;

			// 1MB ずつ書く。進捗は 4MB 進むか書き終えたときだけ出す（1MB ごとの通知は重いだけ）
			private static void WriteWithProgress(FileStream output, byte[] data, int length)
			{
				if (length < ProgressMinBytes)
				{
					output.Write(data, 0, length);
					return;
				}
				int written = 0, lastShown = 0;
				while (written < length)
				{
					int count = Math.Min(WriteChunkSize, length - written);
					output.Write(data, written, count);
					written += count;
					if (written - lastShown >= ProgressMinBytes || written >= length)
					{
						lastShown = written;
						TransferNotice.ShowProgress("シーンを書き込み中",
							ToMegabytes(written) + " / " + ToMegabytes(length) + " MB", (float)written / length);
					}
				}
			}

			private static string ToMegabytes(long bytes)
			{
				return (bytes / 1048576d).ToString("0.0");
			}

			private static void ShowSaveSummary(int length)
			{
				try
				{
					Stopwatch watch = SceneExplorerPlugin.SaveWatch;
					SceneExplorerPlugin.SaveWatch = null;
					TransferNotice.ClearProgress();
					TransferNotice.Show("保存完了: " + MeTextureDedupe.FormatBytes(length)
						+ (watch == null ? "" : " / " + (watch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒")
						+ " / テクスチャ " + MeTextureDedupe.ChunkTextureCount + " 枚");
				}
				catch (Exception) { }
			}

			private static void TransferBytesCore(byte[] data, int length, string dest)
			{
				string tmp = dest + "." + Guid.NewGuid().ToString("N") + ".part";
				// 旧版は dest.bak へ退避してから公開する（公開に失敗したら戻す）。
				// メモリ完結保存にはローカル原本が無いので、ここで旧版を失うとシーンがどこにも無くなる
				string bak = dest + ".bak";
				Exception lastError = null;
				try
				{
					for (int i = 0; i < TransferRetryAttempts; i++)
					{
						try
						{
							Directory.CreateDirectory(Path.GetDirectoryName(dest));
							// 前回の試行で dest を .bak へ退避したままなら先に戻す（常に dest に旧版がある状態から始める）
							if (!File.Exists(dest) && File.Exists(bak)) File.Move(bak, dest);
							using (FileStream output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
							{
								WriteWithProgress(output, data, length);
								output.Flush();
#pragma warning disable 618 // net35 に FileStream.Flush(bool) は無い
								if (!FlushFileBuffers(output.Handle)) throw new IOException("FlushFileBuffers に失敗しました: " + dest);
#pragma warning restore 618
							}
							long actual = new FileInfo(tmp).Length;
							if (actual != length) throw new IOException("サイズ不一致: " + actual + " != " + length);
							bool backedUp = false;
							if (File.Exists(dest))
							{
								if (File.Exists(bak)) File.Delete(bak);
								File.Move(dest, bak);
								backedUp = true;
							}
							try
							{
								File.Move(tmp, dest);
							}
							catch (Exception)
							{
								// 公開に失敗したら旧版を戻す（シーンを失わないことを優先する）
								if (backedUp && !File.Exists(dest))
								{
									try { File.Move(bak, dest); } catch (Exception restoreEx) { Log.LogError("旧版を戻せませんでした（" + bak + " に退避されています）: " + restoreEx.Message); }
								}
								throw;
							}
							if (backedUp) { try { File.Delete(bak); } catch (Exception) { } }
							Log.LogInfo("シーンを書き出しました（メモリから）: " + dest);
							ShowSaveSummary(length);
							return;
						}
						catch (IOException io) { lastError = io; }
						catch (UnauthorizedAccessException ua) { lastError = ua; }
						try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
						if (i < TransferRetryAttempts - 1)
						{
							Log.LogWarning("書き出しに失敗。リトライします (" + (i + 1) + "/" + TransferRetryAttempts + "): " + dest + " / " + lastError.Message);
							Thread.Sleep(TransferRetryIntervalMs);
						}
					}
					string lastReason = lastError == null ? "" : " 最後の失敗理由: " + lastError.Message;
					Log.LogError("シーンを書き出せませんでした（旧版は残しています。メモリ上のデータは失われました）: " + dest + lastReason);
					TransferNotice.Show("シーンを保存できませんでした（旧版は残っています）。\n" + Path.GetFileName(dest) + lastReason);
				}
				catch (Exception ex)
				{
					Log.LogError("シーンを書き出せませんでした（メモリ上のデータなのでローカルには残りません）: " + ex);
					TransferNotice.Show("シーンを保存できませんでした。\n" + Path.GetFileName(dest) + "\n" + ex.Message);
				}
				finally
				{
					try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
				}
			}

			/// <summary>ネットワークへ転送する。本文は一意な .part に書き、FlushFileBuffers（SMB2 FLUSH）でサーバー側の永続化を待ち、
			/// サイズ一致を確認してから同一ディレクトリ内 File.Move で本名公開する。
			/// 本名での公開は File.Move の 1 回だけなので、中断されたファイルが本名で現れることはない。
			/// 検証成功後にのみ File.Move と File.Delete(src) を行う（失敗時はローカル原本を保持して通知する）。
			/// 内容のバイト照合は行わない（1.1GB の保存が約4分になるため）。
			/// したがって「サイズは正しいが内容が化けた」破損は、サーバー側の異常に起因するものを含めて検出できない。
			/// 検出できないもの: File.Move 後のサーバー側破損、転送完了後の別プロセスによる上書き、
			/// FlushFileBuffers が成功した後にサーバーがデータを失う異常。</summary>
			private static void TransferNowCore(string src, string dest)
			{
				string tmp = dest + "." + Guid.NewGuid().ToString("N") + ".part";
				Exception lastError = null;
				bool destRemoved = false;
				try
				{
					for (int i = 0; i < TransferRetryAttempts; i++)
					{
						try
						{
							Directory.CreateDirectory(Path.GetDirectoryName(dest));
							long expected;
							using (FileStream input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
							using (FileStream output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
							{
								expected = input.Length;
								// net35 に Stream.CopyTo は無い
								byte[] buffer = new byte[1024 * 1024];
								int read;
								while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
								{
									output.Write(buffer, 0, read);
								}
								output.Flush();
								// net35 に FileStream.Flush(bool) は無い。SMB2 FLUSH を発行してサーバー側の永続化まで待つ
#pragma warning disable 618
								bool flushed = FlushFileBuffers(output.Handle);
#pragma warning restore 618
								if (!flushed) throw new IOException("FlushFileBuffers に失敗しました: " + dest);
							}
							long actual = new FileInfo(tmp).Length;
							if (actual != expected) throw new IOException("サイズ不一致: " + actual + " != " + expected);
							// shortcut: 内容のバイト照合は行わない（1.1GB の保存が約4分になるため）。
							// ライトバックキャッシュ問題は FlushFileBuffers で塞ぐ。ACK 後にサーバーがデータを失う異常は未検出。
							if (File.Exists(dest)) { File.Delete(dest); destRemoved = true; }
							File.Move(tmp, dest);
							Log.LogInfo("ネットワークへ転送しました: " + dest);
							try
							{
								File.Delete(src);
							}
							catch (Exception ex)
							{
								Log.LogWarning("転送は成功しましたがローカル原本を削除できませんでした: " + src + " / " + ex.Message);
							}
							return;
						}
						catch (FileNotFoundException)
						{
							// 排他により先行の転送が完了して原本が削除済み（または tmp が外部から消された）
							Log.LogInfo("転送対象が既に処理済みのため転送を中止しました: " + src);
							return;
						}
						catch (IOException io) { lastError = io; }
						catch (UnauthorizedAccessException ua) { lastError = ua; }
						try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
						if (i < TransferRetryAttempts - 1)
						{
							Log.LogWarning("転送または検証に失敗（KK_CardCompressionの書き込み中？）。リトライします (" + (i + 1) + "/" + TransferRetryAttempts + "): " + dest + (destRemoved ? "（旧版は削除済み）" : "") + " / " + lastError.Message);
							Thread.Sleep(TransferRetryIntervalMs);
						}
					}
					string lastReason = lastError == null ? "" : " 最後の失敗理由: " + lastError.Message;
					Log.LogError("ネットワーク転送に失敗しました（ローカルに残します）: " + src + lastReason + (destRemoved ? "（旧版は削除済み）" : ""));
					TransferNotice.Show("NAS へ保存できませんでした。ローカルに残しています。\n" + Path.GetFileName(src) + (lastError == null ? "" : "\n" + lastError.Message) + (destRemoved ? "（旧版は削除済み）" : ""));
				}
				catch (Exception ex)
				{
					Log.LogError("ネットワーク転送に失敗しました（ローカルに残します）: " + ex.ToString());
					TransferNotice.Show("NAS へ保存できませんでした。ローカルに残しています。\n" + Path.GetFileName(src) + "\n" + ex.Message);
				}
				finally
				{
					try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
				}
			}
		}
	}
}
