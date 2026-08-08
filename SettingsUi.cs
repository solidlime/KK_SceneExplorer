using System.Collections.Generic;
using UnityEngine;

namespace KK_SceneExplorer
{
	public class SettingsUi : MonoBehaviour
	{
		private Rect windowRect = new Rect(20, 20, 480, 400);
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
		}

		private void OnGUI()
		{
			if (!visible)
			{
				return;
			}
			windowRect = GUI.Window(981234, windowRect, DrawWindow, "フォルダ設定（シーン / キャラ / 衣装）");
		}

		private void DrawWindow(int id)
		{
			GUI.DragWindow(new Rect(0, 0, 10000, 20));

			if (needRefresh)
			{
				RefreshStatuses();
				needRefresh = false;
			}

			scroll = GUILayout.BeginScrollView(scroll);

			// ── シーン ──
			GUILayout.Label("シーン（ローカル: UserData\\studio\\scene は常に参照）");
			DrawFolderSection(ref newScenePath, cachedSceneStatuses,
				new List<string>(SceneExplorerPlugin.ScenePaths.GetConfiguredSceneFolders()),
				SaveSceneFolders);

			GUILayout.Space(10);

			// ── キャラ ──
			GUILayout.Label("キャラ（配下の female/male を女/男タブで自動参照）");
			GUILayout.Label("キャラフォルダの配下 female/male を女/男タブで自動参照します", HelpStyle());
			DrawFolderSection(ref newCharaPath, cachedCharaStatuses,
				SplitRaw(SceneExplorerPlugin.CharaFolders.Value),
				SaveCharaFolders);

			GUILayout.Space(10);

			// ── 衣装 ──
			GUILayout.Label("衣装（直下を参照）");
			GUILayout.Label("衣装フォルダ直下を参照します", HelpStyle());
			DrawFolderSection(ref newCoordinatePath, cachedCoordinateStatuses,
				SplitRaw(SceneExplorerPlugin.CoordinateFolders.Value),
				SaveCoordinateFolders);

			GUILayout.EndScrollView();

			if (GUILayout.Button("再スキャン"))
			{
				needRefresh = true;
			}

			GUILayout.BeginHorizontal();
			GUILayout.Label("フォントサイズ");
			int newFontSize = (int)GUILayout.HorizontalSlider((float)SceneExplorerPlugin.FontSize.Value, 8f, 32f, GUILayout.Width(200));
			GUILayout.Label(newFontSize.ToString());
			GUILayout.EndHorizontal();
			if (newFontSize != SceneExplorerPlugin.FontSize.Value)
			{
				SceneExplorerPlugin.FontSize.Value = newFontSize;
				SceneExplorerPlugin.ConfigFile.Save();
				SceneExplorerPlugin.ResetBrowserStyles();
			}

			GUILayout.Space(8);
			GUILayout.Label("パスはスラッシュ(/)区切りで入力してください（例: //nas/Data/test）。バックスラッシュ形式に自動変換されます");
			GUILayout.Label("「シーンを開く」ダイアログ表示中はツリーが自動表示されます（ローカルとネットワークを一つのツリーでブラウズ）。選んだフォルダのシーンのみ一覧に出ます");
			GUILayout.Label("「シーンを開く」ダイアログは開くたびに自動再スキャンされます");
			GUILayout.Label("ネットワークドライブがゲームから見えない場合（管理者権限起動など）は、ドライブレター(B:\\〜)ではなく UNC パス(\\\\nas\\〜)を指定してください");
			GUILayout.Label("統合ツリーがシーン一覧ダイアログに表示されます（ローカルとネットワークを同じ階層でブラウズ）。BrowserFolders導入時はBFのツリーの代わりに統合ツリーが表示されます");
		}

		// ヘルプ行のスタイル（既存 GUILayout.Label と同様の見た目。細字化したい場合はここで調整）
		private static GUIStyle HelpStyle()
		{
			GUIStyle style = new GUIStyle(GUI.skin.label);
			style.fontSize = GUI.skin.label.fontSize - 1;
			return style;
		}

		/// <summary>フォルダ一覧セクションの共通描画（一覧 + 削除 + 追加入力 + 追加ボタン）</summary>
		private void DrawFolderSection(ref string newPath, FolderStatus[] statuses, List<string> folderList, System.Action<List<string>> save)
		{
			for (int i = 0; i < folderList.Count; i++)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label(FormatStatus(folderList[i], statuses, i));
				if (GUILayout.Button("削除", GUILayout.Width(60)))
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
			if (GUILayout.Button("追加", GUILayout.Width(60)))
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
