using UnityEngine;

namespace KK_SceneExplorer
{
	/// <summary>転送失敗などを Studio 画面右下に短時間表示する。任意のスレッドから Show を呼んでよい。</summary>
	internal class TransferNotice : MonoBehaviour
	{
		private const float DisplaySeconds = 12f;
		private static readonly object Gate = new object();
		private static string pendingMessage;
		private static string message;
		private static float expireAt;
		private GUIStyle style;
		private Texture2D background;

		public static void Show(string text)
		{
			lock (Gate) { pendingMessage = text; }
		}

		private void Update()
		{
			lock (Gate)
			{
				if (pendingMessage == null) return;
				message = pendingMessage;
				pendingMessage = null;
				expireAt = Time.realtimeSinceStartup + DisplaySeconds;
			}
		}

		private void OnGUI()
		{
			string text = message;
			if (text == null) return;
			if (Time.realtimeSinceStartup > expireAt) { message = null; return; }
			if (style == null)
			{
				style = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 14, alignment = TextAnchor.UpperLeft };
				background = new Texture2D(1, 1);
				background.SetPixel(0, 0, Color.white);
				background.Apply();
			}
			const float width = 460f, height = 92f;
			Rect rect = new Rect(Screen.width - width - 24f, Screen.height - height - 24f, width, height);
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
	}
}
