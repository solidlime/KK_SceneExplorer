using System;
using System.Diagnostics;
using UnityEngine;

namespace KK_SceneExplorer
{
	/// <summary>転送失敗などを Studio 画面右下に短時間表示する。任意のスレッドから Show を呼んでよい。</summary>
	internal class TransferNotice : MonoBehaviour
	{
		private const float DisplaySeconds = 12f;
		private const float ProgressSeconds = 3f;
		private static readonly object Gate = new object();
		private static string pendingMessage;
		private static string message;
		private static float expireAt;

		// 進捗（ロード/セーブ中）。更新が ProgressSeconds 途絶えたら消える
		private static string pendingTitle;
		private static string pendingDetail;
		private static float pendingRatio;
		private static string title;
		private static string detail;
		private static float ratio;
		private static float progressExpireAt;
		private static bool clearRequested;

		private GUIStyle style;
		private GUIStyle titleStyle;
		private Texture2D background;

		public static void Show(string text)
		{
			lock (Gate) { pendingMessage = text; }
		}

		/// <summary>進捗バーを表示する。ratio は 0..1。呼ぶたびに表示期限が 3 秒延びる。</summary>
		public static void ShowProgress(string title, string detail, float ratio)
		{
			try
			{
				lock (Gate)
				{
					pendingTitle = title;
					pendingDetail = detail;
					pendingRatio = ratio;
					// Time はメインスレッド専用なので、受け取った時点（Update 側）で満了時刻を決める
				}
			}
			catch (Exception) { }
		}

		public static void ClearProgress()
		{
			try
			{
				lock (Gate)
				{
					pendingTitle = null;
					clearRequested = true;
				}
			}
			catch (Exception) { }
		}

		private static float memorySampleAt = -999f;
		private static long memoryWorkingSet;
		private static long memoryGc;

		/// <summary>cfg が ON のときだけメモリ使用量。500ms 間隔でキャッシュする。</summary>
		private static string MemorySuffix()
		{
			if (SceneExplorerPlugin.ShowProgressMemory == null || !SceneExplorerPlugin.ShowProgressMemory.Value) return null;
			float now = Time.realtimeSinceStartup;
			if (now - memorySampleAt >= 0.5f)
			{
				memorySampleAt = now;
				using (Process process = Process.GetCurrentProcess())
				{
					memoryWorkingSet = process.WorkingSet64;
				}
				memoryGc = GC.GetTotalMemory(false);
			}
			return " / " + ToMegabytes(memoryWorkingSet) + "MB (GC " + ToMegabytes(memoryGc) + "MB)";
		}

		private static string ToMegabytes(long bytes)
		{
			return (bytes / 1048576L).ToString("N0");
		}

		private void Update()
		{
			lock (Gate)
			{
				if (clearRequested)
				{
					clearRequested = false;
					title = null;
					detail = null;
				}
				if (pendingTitle != null)
				{
					title = pendingTitle;
					detail = pendingDetail;
					ratio = pendingRatio;
					progressExpireAt = Time.realtimeSinceStartup + ProgressSeconds;
					pendingTitle = null;
					pendingDetail = null;
				}
				if (pendingMessage == null) return;
				message = pendingMessage;
				pendingMessage = null;
				expireAt = Time.realtimeSinceStartup + DisplaySeconds;
			}
		}

		/// <summary>表示枠（右下）。メッセージと進捗で共用する。</summary>
		private Rect GetRect()
		{
			const float width = 460f, height = 92f;
			return new Rect(Screen.width - width - 24f, Screen.height - height - 24f, width, height);
		}

		private void EnsureStyle()
		{
			if (style != null) return;
			style = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 14, alignment = TextAnchor.UpperLeft };
			titleStyle = new GUIStyle(style) { fontSize = 15, fontStyle = FontStyle.Bold };
			background = new Texture2D(1, 1);
			background.SetPixel(0, 0, Color.white);
			background.Apply();
		}

		private void OnGUI()
		{
			// 進捗があれば進捗だけ、無ければメッセージを出す（排他）
			string progressTitle = title;
			if (progressTitle != null)
			{
				if (Time.realtimeSinceStartup > progressExpireAt) { title = null; detail = null; }
				else { DrawProgress(progressTitle); return; }
			}

			string text = message;
			if (text == null) return;
			if (Time.realtimeSinceStartup > expireAt) { message = null; return; }
			EnsureStyle();
			Rect rect = GetRect();
			Color old = GUI.color;
			int oldDepth = GUI.depth;
			GUI.depth = -2000;
			GUI.color = new Color(0f, 0f, 0f, 0.85f);
			GUI.DrawTexture(rect, background);
			GUI.color = new Color(1f, 0.45f, 0.45f, 1f);
			GUI.Label(new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, rect.height - 20f), text, style);
			GUI.color = old;
			GUI.depth = oldDepth;
		}

		private void DrawProgress(string progressTitle)
		{
			EnsureStyle();
			Rect rect = GetRect();
			Color old = GUI.color;
			int oldDepth = GUI.depth;
			GUI.depth = -2000;

			GUI.color = new Color(0f, 0f, 0f, 0.85f);
			GUI.DrawTexture(rect, background);

			GUI.color = new Color(1f, 0.85f, 0.45f, 1f);
			GUI.Label(new Rect(rect.x + 12f, rect.y + 8f, rect.width - 24f, 22f), progressTitle, titleStyle);

			GUI.color = Color.white;
			string line = detail;
			string memory = "";
			try { memory = MemorySuffix(); } catch (Exception) { }
			if (memory != null) line = line + memory;
			GUI.Label(new Rect(rect.x + 12f, rect.y + 32f, rect.width - 24f, 22f), line, style);

			// バー: ボックス下端から 12px 上、高さ 8px
			Rect bar = new Rect(rect.x + 12f, rect.yMax - 20f, rect.width - 24f, 8f);
			GUI.color = new Color(1f, 1f, 1f, 0.18f);
			GUI.DrawTexture(bar, background);
			float clamped = ratio;
			if (clamped < 0f) clamped = 0f;
			if (clamped > 1f) clamped = 1f;
			GUI.color = new Color(0.4f, 0.9f, 0.4f, 1f);
			GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * clamped, bar.height), background);

			GUI.color = old;
			GUI.depth = oldDepth;
		}
	}
}
