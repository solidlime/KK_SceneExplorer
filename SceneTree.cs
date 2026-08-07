using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Manager;
using UnityEngine;

namespace KK_SceneExplorer
{
	public class NetworkSceneTree : MonoBehaviour
	{
		private Rect windowRect = new Rect(0, 0, 200, 400);
		private bool visible;
		private bool placed;
		private Vector2 scroll;
		private string selectedNode;
		private string searchText = "";
		private HashSet<string> expanded = new HashSet<string>();
		private Dictionary<string, string[]> childrenCache = new Dictionary<string, string[]>();
		private Dictionary<string, bool> errorCache = new Dictionary<string, bool>();
		private float lastCheckTime;

		private const float IndentPerLevel = 12f;
		private const float ToggleWidth = 18f;
		private const float TreeLineWidth = 14f;
		private const float RowPadRight = 8f;

		private GUIStyle selectedRowStyle;
		private Texture2D selectedRowTex;
		private GUIStyle nodeButtonStyle;
		private bool stylesReady;

		private void Awake()
		{
			selectedRowTex = new Texture2D(1, 1);
			selectedRowTex.SetPixel(0, 0, new Color(0.24f, 0.52f, 0.84f, 0.45f));
			selectedRowTex.Apply();
		}

		private void Update()
		{
			if (Time.realtimeSinceStartup - lastCheckTime < 0.3f)
			{
				return;
			}
			lastCheckTime = Time.realtimeSinceStartup;
			visible = UnityEngine.Object.FindObjectOfType<Studio.SceneLoadScene>() != null;
			if (!visible)
			{
				selectedNode = null;
			}
		}

		private void OnGUI()
		{
			if (!visible)
			{
				return;
			}
			if (!placed)
			{
				windowRect = new Rect(0, 0, Mathf.Max(220, Screen.width * 0.15f), Screen.height);
				placed = true;
			}
			windowRect = GUI.Window(981235, windowRect, DrawWindow, "\u30B7\u30FC\u30F3\u30D5\u30A9\u30EB\u30C0");
		}

		private void DrawWindow(int id)
		{
			if (!stylesReady)
			{
				selectedRowStyle = new GUIStyle(GUI.skin.label);
				selectedRowStyle.normal.background = selectedRowTex;
				selectedRowStyle.hover.background = selectedRowTex;

				nodeButtonStyle = new GUIStyle(GUI.skin.label);
				nodeButtonStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
				nodeButtonStyle.hover.textColor = Color.white;
				nodeButtonStyle.alignment = TextAnchor.MiddleLeft;
				nodeButtonStyle.clipping = TextClipping.Clip;
				nodeButtonStyle.wordWrap = false;
				nodeButtonStyle.padding = new RectOffset(2, 2, 1, 1);

				stylesReady = true;
			}

			DrawCurrentPath();

			DrawSearchBox();

			scroll = GUILayout.BeginScrollView(scroll);

			string localRoot = UserData.Create("studio/scene");
			DrawNode(localRoot, 0, true, true);

			string[] roots = SceneExplorerPlugin.ScenePaths.GetConfiguredSceneFolders();
			for (int r = 0; r < roots.Length; r++)
			{
				DrawNode(roots[r], 0, false, r == roots.Length - 1);
			}

			GUILayout.EndScrollView();

			DrawRefreshButton();

			DrawTooltip();

			GUI.DragWindow(new Rect(0, 0, 10000, 24));
		}

		private void DrawCurrentPath()
		{
			string text = GetCurrentDisplay();
			GUILayout.Label(text);
		}

		private void DrawSearchBox()
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label("\u691C\u7D22:", GUILayout.Width(36));
			searchText = GUILayout.TextField(searchText);
			if (!string.IsNullOrEmpty(searchText))
			{
				if (GUILayout.Button("\u00D7", GUILayout.Width(22)))
				{
					searchText = "";
					GUI.FocusControl(null);
				}
			}
			GUILayout.EndHorizontal();
		}

		private void DrawRefreshButton()
		{
			if (GUILayout.Button("\u66F4\u65B0"))
			{
				childrenCache.Clear();
				errorCache.Clear();
				RefreshDialogList();
			}
		}

		private void DrawTooltip()
		{
			string tip = GUI.tooltip;
			if (tip.Length > 0)
			{
				GUILayout.Label(tip, GUILayout.MaxWidth(windowRect.width - 12));
			}
		}

		private string GetCurrentDisplay()
		{
			if (!string.IsNullOrEmpty(SceneExplorerPlugin.currentSceneFolder))
			{
				return "\u73FE\u5728: " + SceneExplorerPlugin.currentSceneFolder.Replace('\\', '/');
			}
			if (!string.IsNullOrEmpty(SceneExplorerPlugin.currentLocalFolder))
			{
				return "\u73FE\u5728: \u30ED\u30FC\u30AB\u30EB - " + SceneExplorerPlugin.currentLocalFolder.Replace('\\', '/');
			}
			return "\u73FE\u5728: \u30ED\u30FC\u30AB\u30EB\uFF08\u65E2\u5B9A\uFF09";
		}

		private void DrawNode(string path, int depth, bool isLocal, bool isLastChild)
		{
			string[] children = GetChildrenCached(path);
			string[] visibleChildren = FilterChildren(children);
			bool hasChildren = visibleChildren.Length > 0;
			bool isExpanded = expanded.Contains(path);
			bool isSelected = selectedNode == path;

			Rect rowRect = GUILayoutUtility.GetRect(
				windowRect.width - 4,
				GUI.skin.label.lineHeight + 4,
				GUILayout.ExpandWidth(false)
			);

			if (isSelected)
			{
				GUI.DrawTexture(rowRect, selectedRowTex);
			}

			float x = rowRect.x + 4;
			float y = rowRect.y + 2;
			float availableForName = rowRect.width - 8;

			for (int d = 0; d < depth; d++)
			{
				Rect lineRect = new Rect(x + d * IndentPerLevel + 5, y, 1, rowRect.height);
				GUI.DrawTexture(lineRect, Texture2D.whiteTexture);
				availableForName -= IndentPerLevel;
			}

			if (depth > 0)
			{
				float branchX = x + (depth - 1) * IndentPerLevel;
				string branch = isLastChild ? "\u2514\u2500\u2500 " : "\u251C\u2500\u2500 ";
				Rect branchRect = new Rect(branchX, y, IndentPerLevel, rowRect.height);
				GUI.Label(branchRect, branch, nodeButtonStyle);
			}

			float toggleX = x + depth * IndentPerLevel;
			availableForName -= IndentPerLevel + RowPadRight;

			if (hasChildren)
			{
				string toggle = isExpanded ? "\u25BC" : "\u25B6";
				Rect toggleRect = new Rect(toggleX, y, ToggleWidth, rowRect.height);
				if (GUI.Button(toggleRect, toggle, nodeButtonStyle))
				{
					if (isExpanded)
					{
						expanded.Remove(path);
					}
					else
					{
						expanded.Add(path);
					}
				}
			}

			float nameX = toggleX + ToggleWidth;
			availableForName -= ToggleWidth;

			string display = GetNodeName(path, isLocal);
			if (errorCache.ContainsKey(path))
			{
				display = display + " \u2717";
			}

			display = TruncateText(display, availableForName);

			GUIContent content = new GUIContent(display, path);
			Rect nameRect = new Rect(nameX, y, availableForName, rowRect.height);

			if (isSelected)
			{
				nodeButtonStyle.normal.textColor = new Color(0.6f, 0.88f, 1f);
			}
			else
			{
				nodeButtonStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
			}

			if (GUI.Button(nameRect, content, nodeButtonStyle))
			{
				if (isLocal)
				{
					SelectLocalFolder(path);
				}
				else
				{
					SelectNode(path);
				}
			}

			if (isExpanded)
			{
				for (int i = 0; i < visibleChildren.Length; i++)
				{
					DrawNode(visibleChildren[i], depth + 1, false, i == visibleChildren.Length - 1);
				}
			}
		}

		private string TruncateText(string text, float maxWidth)
		{
			if (maxWidth < 20)
			{
				return text;
			}
			Vector2 size = nodeButtonStyle.CalcSize(new GUIContent(text));
			if (size.x <= maxWidth)
			{
				return text;
			}

			float ellipsisWidth = nodeButtonStyle.CalcSize(new GUIContent("\u2026")).x;
			float targetWidth = maxWidth - ellipsisWidth;
			if (targetWidth <= 0)
			{
				return "\u2026";
			}

			int lo = 0;
			int hi = text.Length;
			while (lo < hi)
			{
				int mid = (lo + hi + 1) / 2;
				float w = nodeButtonStyle.CalcSize(new GUIContent(text.Substring(0, mid))).x;
				if (w <= targetWidth)
				{
					lo = mid;
				}
				else
				{
					hi = mid - 1;
				}
			}

			if (lo == 0)
			{
				return "\u2026";
			}
			return text.Substring(0, lo) + "\u2026";
		}

		private string GetNodeName(string path, bool isLocal)
		{
			if (isLocal)
			{
				return "\u30ED\u30FC\u30AB\u30EB";
			}
			string trimmed = path.TrimEnd('\\');
			string name = Path.GetFileName(trimmed);
			if (name.Length == 0)
			{
				name = trimmed;
			}
			return name;
		}

		private string[] FilterChildren(string[] children)
		{
			if (searchText.Length == 0)
			{
				return children;
			}
			List<string> filtered = new List<string>();
			foreach (string child in children)
			{
				string name = GetNodeName(child, false);
				if (name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					filtered.Add(child);
				}
			}
			return filtered.ToArray();
		}

		private string[] GetChildrenCached(string path)
		{
			string[] cached;
			if (childrenCache.TryGetValue(path, out cached))
			{
				return cached;
			}
			List<string> children = new List<string>();
			try
			{
				string[] dirs = Directory.GetDirectories(path);
				foreach (string dir in dirs)
				{
					children.Add(dir);
				}
			}
			catch (Exception ex)
			{
				errorCache[path] = true;
				SceneExplorerPlugin.Log.LogWarning("フォルダの読み込みに失敗しました: " + path + " - " + ex.Message);
			}
			string[] result = children.ToArray();
			childrenCache[path] = result;
			return result;
		}

		private void SelectNode(string path)
		{
			selectedNode = path;
			SceneExplorerPlugin.currentSceneFolder = path;
			SceneExplorerPlugin.Log.LogInfo("ツリー選択: " + path);
			ExpandToParent(path);
			ApplySelection();
		}

		private void SelectLocalFolder(string path)
		{
			selectedNode = path;
			SceneExplorerPlugin.currentLocalFolder = path;
			SceneExplorerPlugin.currentSceneFolder = null;
			SceneExplorerPlugin.Log.LogInfo("ツリー選択: ローカル - " + path);
			ExpandToParent(path);
			ApplySelection();
		}

		private void ExpandToParent(string path)
		{
			string current = path;
			while (!string.IsNullOrEmpty(current))
			{
				expanded.Add(current);
				string parent = Path.GetDirectoryName(current);
				if (parent == null || parent == current)
				{
					break;
				}
				current = parent;
			}
		}

		private void ApplySelection()
		{
			SceneExplorerPlugin.applyingSelection = true;
			RefreshDialogList();
		}

		private void RefreshDialogList()
		{
			if (SceneExplorerPlugin.activeLoadScene == null)
			{
				SceneExplorerPlugin.Log.LogWarning("一覧更新: activeLoadScene が null（ダイアログ未検出）");
				return;
			}
			try
			{
				SceneExplorerPlugin.applyingSelection = true;
				MethodInfo initInfo = AccessTools.Method(typeof(Studio.SceneLoadScene), "InitInfo");
				if (initInfo != null)
				{
					initInfo.Invoke(SceneExplorerPlugin.activeLoadScene, null);
				}
				SceneExplorerPlugin.Log.LogInfo("一覧更新: InitInfo 再実行完了");
				MethodInfo setPage = AccessTools.Method(typeof(Studio.SceneLoadScene), "SetPage", new Type[] { typeof(int) });
				if (setPage != null)
				{
					setPage.Invoke(SceneExplorerPlugin.activeLoadScene, new object[] { 1 });
				}
			}
			catch (Exception ex)
			{
				SceneExplorerPlugin.Log.LogWarning("一覧更新に失敗しました: " + ex.Message);
			}
			finally
			{
				SceneExplorerPlugin.applyingSelection = false;
			}
		}
	}
}
