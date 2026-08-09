using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;   // v3.3.1: 透明 uGUI ブロックレイヤー（Canvas/Image/GraphicRaycaster）

namespace KK_SceneExplorer
{
	public class SettingsUi : MonoBehaviour
	{
		private const int WindowId = 981234;

		private Rect windowRect = new Rect(20, 20, 600, 400);
		private bool visible;
		private string newScenePath = "";
		private string newCharaPath = "";
		private string newCoordinatePath = "";
		private Vector2 scroll;
		private FolderStatus[] cachedSceneStatuses;
		private FolderStatus[] cachedCharaStatuses;
		private FolderStatus[] cachedCoordinateStatuses;
		private bool needRefresh = true;

		private bool wasDown;

		// ── v3.3.1: 透明 uGUI ブロックレイヤー（SceneBrowser と同一パターン）──
		private GameObject _blockLayer;
		private Canvas _blockCanvas;
		private RectTransform _blockRect;

		// ── 静的テクスチャ（SceneBrowser と同じ配色）──
		private static Texture2D _windowBgTex;
		private static Texture2D _titleBarTex;

		// ── スタイル ──
		private bool _stylesReady;
		private GUIStyle _titleBarStyle;
		private GUIStyle _sectionHeaderStyle;
		private GUIStyle _buttonStyle;

		// ── フォント連動 ──
		private float FontSizeVal { get { return (float)SceneExplorerPlugin.FontSize.Value; } }
		private float TextLineHeight { get { return Mathf.Ceil(FontSizeVal * 1.4f); } }
		private float TitleBarHeight { get { return TextLineHeight + 8f; } }

		private void Awake()
		{
			try
			{
				// テクスチャ生成（SceneBrowser と同一配色）
				_windowBgTex = new Texture2D(1, 1);
				_windowBgTex.SetPixel(0, 0, new Color(0.503f, 0.542f, 0.629f, 0.94f)); // SceneBrowser._windowBgTex と同一
				_windowBgTex.Apply();

				_titleBarTex = new Texture2D(1, 1);
				_titleBarTex.SetPixel(0, 0, new Color(0.435f, 0.481f, 0.581f, 1f)); // SceneBrowser._titleBarTex と同一
				_titleBarTex.Apply();

				// v3.3.1: 透明 uGUI ブロックレイヤーを生成（非表示中は Canvas 無効のまま）
				EnsureBlockLayer();
			}
			catch (Exception ex)
			{
				SceneExplorerPlugin.Log.LogWarning("SettingsUi Awake で例外が発生しました: " + ex);
			}
		}

		private void Update()
		{
			bool down = SceneExplorerPlugin.SettingsKey.Value.IsDown();
			if (down && !wasDown)
			{
				visible = !visible;
				if (visible)
				{
					needRefresh = true;
				}
			}
			wasDown = down;

			// v3.3.1: ブロックレイヤーをウィンドウ矩形に追従させる
			UpdateBlockLayer();
		}

		// ═══════════════════════════════════════════════════════
		// 透明 uGUI ブロックレイヤー（SceneBrowser.cs から移植）
		// ═══════════════════════════════════════════════════════

		/// <summary>
		/// 透明 uGUI ブロックレイヤーの生成。
		/// ウィンドウ矩形に追従する ScreenSpaceOverlay の透明 Image で、
		/// ウィンドウ内の背後 uGUI のみクリックをブロックする（ウィンドウ外はゲーム操作可能）。
		/// </summary>
		private void EnsureBlockLayer()
		{
			if (_blockCanvas != null) return;
			try
			{
				var go = new GameObject("SceneExplorerSettingsBlock");
				go.transform.SetParent(transform, false);
				_blockLayer = go;
				_blockCanvas = go.AddComponent<Canvas>();
				_blockCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
				_blockCanvas.sortingOrder = 9999;
				_blockCanvas.enabled = false;       // 非表示中はブロックしない
				// ScreenSpaceOverlay Canvas の RectTransform は Unity が常に全画面サイズに固定するため、
				// Image は子 GameObject に置き、その RectTransform をウィンドウ矩形に合わせる。
				var imgGo = new GameObject("BlockImage");
				imgGo.transform.SetParent(go.transform, false);
				var img = imgGo.AddComponent<Image>();
				img.color = new Color(0f, 0f, 0f, 0f);   // 完全透明（描画はされない）
				img.raycastTarget = true;
				go.AddComponent<GraphicRaycaster>();
				_blockRect = imgGo.GetComponent<RectTransform>();
			}
			catch (Exception ex)
			{
				SceneExplorerPlugin.Log.LogWarning("uGUI ブロックレイヤーの生成に失敗しました: " + ex.Message);
			}
		}

		/// <summary>
		/// ブロックレイヤーをウィンドウ矩形に追従させる。
		/// IMGUI は左上原点、uGUI ScreenSpaceOverlay は左下原点なので y を反転する。
		/// </summary>
		private void UpdateBlockLayer()
		{
			if (_blockCanvas == null) return;
			if (visible)
			{
				if (!_blockCanvas.enabled) _blockCanvas.enabled = true;
				float x = windowRect.x;
				float y = windowRect.y;
				float w = windowRect.width;
				float h = windowRect.height;
				_blockRect.anchorMin = Vector2.zero;
				_blockRect.anchorMax = Vector2.zero;
				_blockRect.pivot = Vector2.zero;
				_blockRect.anchoredPosition = new Vector2(x, Screen.height - (y + h));
				_blockRect.sizeDelta = new Vector2(w, h);
			}
			else if (_blockCanvas.enabled)
			{
				_blockCanvas.enabled = false;
			}
		}

		private void OnDestroy()
		{
			// v3.3.1: 透明 uGUI ブロックレイヤーを破棄
			if (_blockLayer != null) Destroy(_blockLayer);
			_blockCanvas = null;
			_blockRect = null;
			if (_windowBgTex != null) Destroy(_windowBgTex);
			if (_titleBarTex != null) Destroy(_titleBarTex);
		}

		// ═══════════════════════════════════════════════════════
		// スタイル初期化（OnGUI初回のみ、SceneBrowser と同一配色）
		// ═══════════════════════════════════════════════════════

		private void InitStylesOnce()
		{
			if (_stylesReady) return;

			var skin = GUI.skin;
			int fs = SceneExplorerPlugin.FontSize.Value;

			// タイトルバー（SceneBrowser._titleBarStyle と同一）
			_titleBarStyle = new GUIStyle(skin.label);
			_titleBarStyle.normal.background = _titleBarTex;
			_titleBarStyle.normal.textColor = new Color(0.88f, 0.89f, 0.92f);
			_titleBarStyle.fontSize = fs;
			_titleBarStyle.alignment = TextAnchor.MiddleLeft;
			_titleBarStyle.padding = new RectOffset(8, 8, 4, 4);

			// セクション見出し
			_sectionHeaderStyle = new GUIStyle(skin.label);
			_sectionHeaderStyle.normal.textColor = new Color(0.88f, 0.89f, 0.92f);
			_sectionHeaderStyle.fontSize = fs;
			_sectionHeaderStyle.fontStyle = FontStyle.Bold;

			// ボタン（SceneBrowser._toolbarButtonStyle と同系列）
			_buttonStyle = new GUIStyle(skin.button);
			_buttonStyle.fontSize = fs;
			_buttonStyle.padding = new RectOffset(8, 8, 4, 4);

			_stylesReady = true;
		}

		/// <summary>フォントサイズ変更を反映するため、次回OnGUIでスタイルを再生成させる。</summary>
		public void RefreshStyles()
		{
			_stylesReady = false;
		}

		// ═══════════════════════════════════════════════════════
		// OnGUI
		// ═══════════════════════════════════════════════════════

		private void OnGUI()
		{
			if (!visible) return;

			// 他プラグインが GUI.matrix に残した変換を強制リセット
			GUI.matrix = Matrix4x4.identity;

			InitStylesOnce();

			// 最前面に描画
			GUI.depth = -999;

			// フォントサイズに応じてウィンドウ幅を動的調整（fs=20→600px, fs=32→960px）
			windowRect.width = Mathf.Max(580f, SceneExplorerPlugin.FontSize.Value * 30f);

			windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "");
		}

		private void DrawWindow(int id)
		{
			float w = windowRect.width;
			float h = windowRect.height;
			var fullRect = new Rect(0, 0, w, h);

			// ウィンドウ背景（SceneBrowser と同一）
			if (Event.current.type == EventType.Repaint)
			{
				GUI.DrawTexture(fullRect, _windowBgTex, ScaleMode.StretchToFill);
			}

			// タイトルバー（SceneBrowser と同一描画パターン）
			var titleRect = new Rect(0, 0, fullRect.width, TitleBarHeight);
			if (Event.current.type == EventType.Repaint)
			{
				_titleBarStyle.Draw(titleRect, "☁ フォルダ設定（シーン / キャラ / 衣装）", false, false, false, false);
			}

			// ドラッグ領域: タイトルバー全体
			GUI.DragWindow(titleRect);

			// コンテンツ領域（タイトルバー下）
			var contentRect = new Rect(0, titleRect.yMax, fullRect.width, fullRect.height - titleRect.yMax);

			GUILayout.BeginArea(contentRect);

			if (needRefresh)
			{
				RefreshStatuses();
				needRefresh = false;
			}

			scroll = GUILayout.BeginScrollView(scroll);

			// ── シーン ──
			GUILayout.Label("シーン（ローカル: UserData\\studio\\scene は常に参照）", _sectionHeaderStyle);
			DrawFolderSection(ref newScenePath, cachedSceneStatuses,
				new List<string>(SceneExplorerPlugin.ScenePaths.GetConfiguredSceneFolders()),
				SaveSceneFolders);

			GUILayout.Space(10);

			// ── キャラ ──
			GUILayout.Label("キャラ（配下の female/male を女/男タブで自動参照）", _sectionHeaderStyle);
			DrawFolderSection(ref newCharaPath, cachedCharaStatuses,
				SplitRaw(SceneExplorerPlugin.CharaFolders.Value),
				SaveCharaFolders);

			GUILayout.Space(10);

			// ── 衣装 ──
			GUILayout.Label("衣装（直下を参照）", _sectionHeaderStyle);
			DrawFolderSection(ref newCoordinatePath, cachedCoordinateStatuses,
				SplitRaw(SceneExplorerPlugin.CoordinateFolders.Value),
				SaveCoordinateFolders);

			GUILayout.EndScrollView();

			if (GUILayout.Button("再スキャン", _buttonStyle))
			{
				needRefresh = true;
			}

			GUILayout.BeginHorizontal();
			GUILayout.Label("フォントサイズ", GUILayout.Width(Mathf.Max(80, SceneExplorerPlugin.FontSize.Value * 5)));
			int newFontSize = (int)GUILayout.HorizontalSlider((float)SceneExplorerPlugin.FontSize.Value, 8f, 32f);
			GUILayout.Label(newFontSize.ToString(), GUILayout.Width(40));
			GUILayout.EndHorizontal();
			if (newFontSize != SceneExplorerPlugin.FontSize.Value)
			{
				SceneExplorerPlugin.FontSize.Value = newFontSize;
				SceneExplorerPlugin.ConfigFile.Save();
				SceneExplorerPlugin.ResetBrowserStyles();
				_stylesReady = false;
			}

			GUILayout.EndArea();
		}

		/// <summary>フォルダ一覧セクションの共通描画（一覧 + 削除 + 追加入力 + 追加ボタン）</summary>
		private void DrawFolderSection(ref string newPath, FolderStatus[] statuses, List<string> folderList, System.Action<List<string>> save)
		{
			for (int i = 0; i < folderList.Count; i++)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label(FormatStatus(folderList[i], statuses, i));
				if (GUILayout.Button("削除", _buttonStyle, GUILayout.Width(60)))
				{
					folderList.RemoveAt(i);
					save(folderList);
					needRefresh = true;
					break;
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.BeginHorizontal();
			newPath = GUILayout.TextField(newPath).Replace('\\', '/');
			if (GUILayout.Button("追加", _buttonStyle, GUILayout.Width(60)))
			{
				string trimmed = newPath.Trim();
				if (trimmed.Length > 0)
				{
					trimmed = trimmed.Replace('/', '\\');
					if (trimmed.Length >= 2 && trimmed[0] == '\\' && trimmed[1] != '\\')
					{
						trimmed = "\\" + trimmed;
					}
					folderList.Add(trimmed);
					save(folderList);
					newPath = "";
					needRefresh = true;
				}
			}
			GUILayout.EndHorizontal();
		}

		/// <summary>セミコロン区切りの設定文字列を一覧に分解する</summary>
		private static List<string> SplitRaw(string raw)
		{
			List<string> result = new List<string>();
			if (string.IsNullOrEmpty(raw)) return result;
			foreach (string part in raw.Split(new[] { ';', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries))
			{
				string trimmed = part.Trim();
				if (trimmed.Length > 0) result.Add(trimmed);
			}
			return result;
		}

		private void RefreshStatuses()
		{
			cachedSceneStatuses = SceneExplorerPlugin.ScenePaths.GetSceneFolderStatuses();
			cachedCharaStatuses = SceneExplorerPlugin.ScenePaths.GetFolderStatuses(SceneExplorerPlugin.CharaFolders.Value);
			cachedCoordinateStatuses = SceneExplorerPlugin.ScenePaths.GetFolderStatuses(SceneExplorerPlugin.CoordinateFolders.Value);
		}

		private static string FormatStatus(string path, FolderStatus[] statuses, int index)
		{
			path = path.Replace('\\', '/');
			if (statuses != null && index < statuses.Length)
			{
				FolderStatus status = statuses[index];
				if (status.Error != null)
				{
					return "✗ " + path + "（見つからない）";
				}
				if (status.FileCount > 0)
				{
					return "✓ " + status.FileCount + "件: " + path;
				}
				return "空: " + path;
			}
			return path;
		}

		private static void SaveSceneFolders(List<string> folders)
		{
			SceneExplorerPlugin.SceneFolders.Value = string.Join(";", folders.ToArray());
			SceneExplorerPlugin.ConfigFile.Save();
		}

		private static void SaveCharaFolders(List<string> folders)
		{
			SceneExplorerPlugin.CharaFolders.Value = string.Join(";", folders.ToArray());
			SceneExplorerPlugin.ConfigFile.Save();
		}

		private static void SaveCoordinateFolders(List<string> folders)
		{
			SceneExplorerPlugin.CoordinateFolders.Value = string.Join(";", folders.ToArray());
			SceneExplorerPlugin.ConfigFile.Save();
		}
	}
}
