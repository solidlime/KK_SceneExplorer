using System;
using System.Reflection;
using HarmonyLib;

namespace KK_SceneExplorer
{
	internal static class BfInjection
	{
		internal static Type SceneFoldersType;

		private static string lastWarning;

		internal static void Apply(Harmony harmony)
		{
			MethodInfo targetOnGui = AccessTools.Method(SceneFoldersType, "OnGui");
			if (targetOnGui != null)
			{
				harmony.Patch(targetOnGui, prefix: new HarmonyMethod(AccessTools.Method(typeof(BfInjection), nameof(BfTreeDisablePrefix))));
				SceneExplorerPlugin.Log.LogInfo("BF統合: BFのシーンツリーを無効化しました");
			}
			else
			{
				SceneExplorerPlugin.Log.LogWarning("BF統合: SceneFolders.OnGui が見つかりません（スキップ）");
			}

			MethodInfo targetInit = AccessTools.Method(typeof(Studio.SceneLoadScene), "InitInfo");
			if (targetInit != null)
			{
				harmony.Patch(targetInit, prefix: new HarmonyMethod(AccessTools.Method(typeof(BfInjection), nameof(InitInfoPrefix))));
			}
			else
			{
				SceneExplorerPlugin.Log.LogWarning("BF統合: SceneLoadScene.InitInfo が見つかりません（スキップ）");
			}
		}

		private static bool BfTreeDisablePrefix()
		{
			return false;
		}

		private static void InitInfoPrefix()
		{
			try
			{
				if (SceneFoldersType == null)
				{
					return;
				}
				FieldInfo f = AccessTools.Field(SceneFoldersType, "_currentRelativeFolder");
				if (f != null)
				{
					f.SetValue(null, "studio/scene");
				}
				else
				{
					LogOnce("BF統合: _currentRelativeFolder フィールドが見つかりません");
				}
				Studio.SceneLoadScene.page = 1;
			}
			catch (Exception ex)
			{
				LogOnce("BF統合: _currentRelativeFolder の設定に失敗: " + ex.Message);
			}
		}

		private static void LogOnce(string message)
		{
			if (message != lastWarning)
			{
				lastWarning = message;
				SceneExplorerPlugin.Log.LogWarning(message);
			}
		}
	}
}
