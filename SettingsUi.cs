using System.Collections.Generic;
using UnityEngine;

namespace KK_SceneExplorer
{
	public class SettingsUi : MonoBehaviour
	{
		private Rect windowRect = new Rect(20, 20, 480, 400);
		private bool visible;
		private string newPath = "";
		private Vector2 scroll;
		private FolderStatus[] cachedStatuses;
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
			windowRect = GUI.Window(981234, windowRect, DrawWindow, "Network Scene Folders 設定");
		}

		private void DrawWindow(int id)
		{
			GUI.DragWindow(new Rect(0, 0, 10000, 20));

			GUILayout.Label("シーンフォルダ一覧（ローカル: UserData\\studio\\scene は常に参照）");

			if (needRefresh)
			{
				RefreshStatuses();
				needRefresh = false;
			}

			List<string> folderList = new List<string>(SceneExplorerPlugin.ScenePaths.GetConfiguredSceneFolders());

			scroll = GUILayout.BeginScrollView(scroll);
			for (int i = 0; i < folderList.Count; i++)
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label(FormatStatus(folderList[i], cachedStatuses, i));
				if (GUILayout.Button("削除", GUILayout.Width(60)))
				{
					folderList.RemoveAt(i);
					SaveFolders(folderList);
					needRefresh = true;
					break;
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.EndScrollView();

			if (GUILayout.Button("再スキャン"))
			{
				needRefresh = true;
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
					SaveFolders(folderList);
					newPath = "";
					needRefresh = true;
				}
			}
			GUILayout.EndHorizontal();

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

			// v3.0.7: サムネイル明るさスライダー
			GUILayout.BeginHorizontal();
			GUILayout.Label("サムネ明るさ");
			float newBrightness = GUILayout.HorizontalSlider(SceneExplorerPlugin.ThumbBrightness.Value, 0.8f, 1.5f, GUILayout.Width(200));
			GUILayout.Label(newBrightness.ToString("F2"));
			GUILayout.EndHorizontal();
			if (Mathf.Abs(newBrightness - SceneExplorerPlugin.ThumbBrightness.Value) > 0.001f)
			{
				SceneExplorerPlugin.ThumbBrightness.Value = newBrightness;
				SceneExplorerPlugin.ConfigFile.Save();
				SceneExplorerPlugin.ResetThumbnailBrightness();
			}

			// v3.0.7: サムネイルコントラストスライダー
			GUILayout.BeginHorizontal();
			GUILayout.Label("サムネコントラスト");
			float newContrast = GUILayout.HorizontalSlider(SceneExplorerPlugin.ThumbContrast.Value, 0.7f, 1.1f, GUILayout.Width(200));
			GUILayout.Label(newContrast.ToString("F2"));
			GUILayout.EndHorizontal();
			if (Mathf.Abs(newContrast - SceneExplorerPlugin.ThumbContrast.Value) > 0.001f)
			{
				SceneExplorerPlugin.ThumbContrast.Value = newContrast;
				SceneExplorerPlugin.ConfigFile.Save();
				SceneExplorerPlugin.ResetThumbnailBrightness();
			}

			GUILayout.Space(8);
			GUILayout.Label("パスはスラッシュ(/)区切りで入力してください（例: //nas/Data/test）。バックスラッシュ形式に自動変換されます");
			GUILayout.Label("「シーンを開く」ダイアログ表示中はツリーが自動表示されます（ローカルとネットワークを一つのツリーでブラウズ）。選んだフォルダのシーンのみ一覧に出ます");
			GUILayout.Label("「シーンを開く」ダイアログは開くたびに自動再スキャンされます");
			GUILayout.Label("ネットワークドライブがゲームから見えない場合（管理者権限起動など）は、ドライブレター(B:\\〜)ではなく UNC パス(\\\\nas\\〜)を指定してください");
			GUILayout.Label("統合ツリーがシーン一覧ダイアログに表示されます（ローカルとネットワークを同じ階層でブラウズ）。BrowserFolders導入時はBFのツリーの代わりに統合ツリーが表示されます");
		}

		private void RefreshStatuses()
		{
			cachedStatuses = SceneExplorerPlugin.ScenePaths.GetSceneFolderStatuses();
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
					return "✓ " + status.FileCount + "シーン: " + path;
				}
				return "空: " + path + "（この場所にシーンなし）";
			}
			return path;
		}

		private static void SaveFolders(List<string> folders)
		{
			SceneExplorerPlugin.SceneFolders.Value = string.Join(";", folders.ToArray());
			SceneExplorerPlugin.ConfigFile.Save();
		}
	}
}
