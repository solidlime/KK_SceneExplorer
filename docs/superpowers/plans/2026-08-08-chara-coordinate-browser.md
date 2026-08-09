# キャラ/衣装ブラウザ統合 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 既存の SceneBrowser（シーンロード用）をキャラ追加（女/男）・衣装変更の3モードでも使えるようにし、標準の CharaList パネルと MPCharCtrl のコスチュームタブを自動的に差し替える。

**Architecture:** `BrowserMode` enum（Scene/CharaFemale/CharaMale/Coordinate）でブラウザの対象を切り替える。起動は標準 UI のタブ操作を Harmony で監視して検知（キャラ = `AddButtonCtrl.OnClick` Postfix で「CharaList が active になったか」を判定、衣装 = `MPCharCtrl.OnClickRoot` Prefix で `_idx==4` を検知）し、標準パネルを非表示にして SceneBrowser を表示。ロードは既存の `Studio.AddFemale/AddMale` と `OCIChar.LoadClothesFile` を直接呼ぶ（CharaList 本体と同じ経路）。ファイル一覧・サムネ・非同期ロード・キャッシュは SceneBrowser の既存機構を流用。

**Tech Stack:** C# (net35) / BepInEx 5 / HarmonyLib / Unity IMGUI（OnGUI 毎フレーム描画）

## Global Constraints

- KK のみビルド（csproj の Game プロパティ未指定 = KK/net35）。KKS はこの環境に KoikatsuSunshine_Data が無いためビルドしない
- net35 制約: async/await・ConcurrentQueue は使わない（既存の ThreadPool+Queue 方式に従う）
- 既存の実装を壊さない: 非同期サムネロード・可視範囲カリング・ガンマ補正・サムネキャッシュ(300枚LRU)・EventSystem 無効化（_lockedEventSystem 参照保持）・スプリッター位置保存・ボトムバー余白(BottomBarRightPadding=24f)
- DLL 配置: PowerShell の `Copy-Item` は `-LiteralPath` 必須（`[MODDING]` を含むパスはワイルドカード解釈される）
- コミットは機能単位・日本語説明文
- キャラカードのプレビュー PNG は 240x320（3:4 縦長）: 既存の 16:9 サムネ領域に ScaleToFit で収まる（左右に余白が出るのは仕様）
- カードバイナリの fullname は IDAT（zlib 圧縮）内にあり直接読まない → 実行時 `ChaFileControl.LoadCharaFile(path, sex, noLoadPng: true)` で取得（標準 CharaList と同一方式）
- 参照追加は不要: CharaList / MPCharCtrl / ChaFileControl / ChaFileCoordinate は全て Assembly-CSharp（既存参照）内

---

### Task 1: BrowserMode 基盤（SceneExplorerPlugin.cs）

**Files:**
- Modify: `SceneExplorerPlugin.cs:57` 付近（`activeLoadScene` 宣言の隣）

**Interfaces:**
- Produces: `SceneExplorerPlugin.BrowserMode` enum / `SceneExplorerPlugin.CurrentBrowserMode`（静的プロパティ）/ `SceneExplorerPlugin.GetModeRootFolder()` / `SceneExplorerPlugin.RequestCharaMode(...)` / `SceneExplorerPlugin.RequestSceneMode()`

- [ ] **Step 1: enum と静的状態を追加**

`SceneExplorerPlugin.cs:57` の `internal static Studio.SceneLoadScene activeLoadScene;` の直後に追加:

```csharp
/// <summary>ブラウザの操作対象モード（v3.1.0: キャラ/衣装対応）</summary>
public enum BrowserMode { Scene, CharaFemale, CharaMale, Coordinate }

internal static Studio.CharaList activeCharaList;   // 最後に Awake した CharaList（表示監視用）
internal static BrowserMode CurrentBrowserMode = BrowserMode.Scene;
```

- [ ] **Step 2: モード切替ヘルパーを追加**（同じファイル、`activeLoadScene` 宣言の直後、静的メソッド領域）

```csharp
/// <summary>モード対応のルートフォルダ。Scene は null（従来動作）。UserData.Path 基準</summary>
public static string GetModeRootFolder()
{
    switch (CurrentBrowserMode)
    {
        case BrowserMode.CharaFemale: return "chara/female";
        case BrowserMode.CharaMale:   return "chara/male";
        case BrowserMode.Coordinate:  return "coordinate";
        default: return null;
    }
}

/// <summary>キャラモード要求。CharaList が active になった時に AddButtonCtrl.OnClick Postfix から呼ばれる</summary>
public static void RequestCharaMode(Studio.CharaList charaList)
{
    if (charaList == null) return;
    int sex = 1;
    try { sex = (int)AccessTools.Field(typeof(Studio.CharaList), "sex").GetValue(charaList); }
    catch (Exception ex) { Log.LogWarning("CharaList.sex 読取失敗: " + ex.Message); }
    CurrentBrowserMode = (sex == 1) ? BrowserMode.CharaFemale : BrowserMode.CharaMale;
    CurrentBrowserFolder = GetModeRootFolder();
    Log.LogInfo("[SceneExplorer] Charaモード: " + CurrentBrowserMode + " folder=" + CurrentBrowserFolder);
}

/// <summary>シーンモードへ戻す。タブ切替・Close・OnClickRoot 他タブから呼ばれる</summary>
public static void RequestSceneMode(string reason)
{
    if (CurrentBrowserMode != BrowserMode.Scene)
        Log.LogInfo("[SceneExplorer] モード解除(" + reason + "): " + CurrentBrowserMode + " -> Scene");
    CurrentBrowserMode = BrowserMode.Scene;
}
```

注: `AccessTools` は既に `using HarmonyLib;` 済み（SceneExplorerPlugin.cs:10）。

- [ ] **Step 3: ビルド確認**

Run: `dotnet build "G:\MyGAME\Koikatsu\[MODDING] Tools\KK_SceneExplorer\KK_SceneExplorer.csproj"`（Set-Location -LiteralPath 'G:\MyGAME\Koikatsu\[MODDING] Tools\KK_SceneExplorer' してから）
Expected: exit 0（既存の SceneBrowser はまだ CurrentBrowserMode を参照しないため挙動不変）

- [ ] **Step 4: コミット**

```bash
git add SceneExplorerPlugin.cs
git commit -m "feat: add BrowserMode enum and mode switch helpers for chara/coordinate browser"
```

---

### Task 2: キャラモード起動/終了フック（AddButtonCtrl.OnClick Postfix + SceneBrowser.Update 統合）

**Files:**
- Modify: `SceneExplorerPlugin.cs`（Patches クラス、`ApplyAll` と Patch メソッド群）
- Modify: `SceneBrowser.cs`（Update 冒頭 :266-283 の EventSystem 制御の直後）

**Interfaces:**
- Consumes: Task 1 の `CurrentBrowserMode` / `RequestCharaMode` / `RequestSceneMode` / `activeCharaList`
- Produces: `Patches.CharaListAwakePostfix(Studio.CharaList __instance)` / `Patches.AddButtonOnClickPostfix()` / SceneBrowser.Update 内の `activeCharaList` 監視ロジック

背景（重要）: `Studio.CharaList` は**常設パネル**で閉じるボタンが無く、`AddButtonCtrl.OnClick(int)` が `commonInfo[i].SetActive` で表示制御する。CharaList に OnEnable/OnDisable は**存在しない**（Awake :243 のみ）ため、OnEnable フックは使えない。代わりに「AddButtonCtrl.OnClick の Postfix で結果として CharaList が active になったかを見る」方式を使う（タブ番号と CharaList の対応関係をコードから知る必要がなくなる）。

- [ ] **Step 1: ApplyAll にパッチ登録を追加**

`SceneExplorerPlugin.cs` の `ApplyAll` 内、t6（SceneInfoSave）登録の後に追加:

```csharp
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
```

- [ ] **Step 2: Patch メソッド2つを追加**（Patches クラス内、`SceneInfoSavePostfix` の後）

```csharp
// v3.1.0: CharaList のインスタンスを保持（Awake はスタジオ起動時に一度だけ呼ばれる）
private static void CharaListAwakePostfix(Studio.CharaList __instance)
{
    SceneExplorerPlugin.activeCharaList = __instance;
}

// v3.1.0: タブ切替後、CharaList が表示状態になったらキャラモード開始（排他制御なので他タブなら自動で非表示になる）
private static void AddButtonOnClickPostfix()
{
    var list = SceneExplorerPlugin.activeCharaList;
    if (list == null) return;
    if (list.gameObject.activeInHierarchy)
    {
        SceneExplorerPlugin.RequestCharaMode(list);
    }
    else if (SceneExplorerPlugin.CurrentBrowserMode != BrowserMode.Scene)
    {
        SceneExplorerPlugin.RequestSceneMode("タブ切替");
    }
}
```

- [ ] **Step 3: SceneBrowser.Update にモード監視を追加**

`SceneBrowser.cs` Update 冒頭（:266-283 の EventSystem 制御ブロックの直後）に追加:

```csharp
// v3.1.0: キャラモード中は CharaList パネルを非表示にして SceneBrowser に差し替える
var cl = SceneExplorerPlugin.activeCharaList;
bool wantChara = SceneExplorerPlugin.CurrentBrowserMode != BrowserMode.Scene &&
                 SceneExplorerPlugin.CurrentBrowserMode != BrowserMode.Coordinate;
if (wantChara && cl != null && cl.gameObject.activeInHierarchy)
{
    cl.gameObject.SetActive(false);
    if (!_visible) _visible = true;   // ShouldBeVisible は CurrentBrowserMode 基準に拡張される（Task 4）
}
```

- [ ] **Step 4: ビルド確認**

Run: `dotnet build`（Task 1 と同じ手順）
Expected: exit 0。この時点では Task 4 未実装のため SceneBrowser はまだ出ない（フックと状態遷移のみ動く）

- [ ] **Step 5: コミット**

```bash
git add SceneExplorerPlugin.cs SceneBrowser.cs
git commit -m "feat: hook CharaList/AddButtonCtrl to detect chara mode start/end"
```

---

### Task 3: 衣装モード起動/終了フック（MPCharCtrl.OnClickRoot Prefix）

**Files:**
- Modify: `SceneExplorerPlugin.cs`（Patches クラス）

**Interfaces:**
- Consumes: Task 1 の `CurrentBrowserMode` / `RequestSceneMode`
- Produces: `Patches.MPCharCtrlOnClickRootPrefix(Studio.MPCharCtrl __instance, int _idx)` — costumeMode 状態は `CurrentBrowserMode == BrowserMode.Coordinate` で表現

背景: MPCharCtrl（キャラ操作パネル）のタブ `OnClickRoot(int _idx)` で `_idx==4` がコスチューム（衣装）。active setter は非表示時 `OnClickRoot(-1)` を呼ぶ（:1524）。Awake 中の初期化 `OnClickRoot(select)`（:1608）が誤発火しないよう `activeInHierarchy` ガードを付ける。

- [ ] **Step 1: ApplyAll にパッチ登録を追加**（Task 2 Step 1 の t8 登録の後）

```csharp
MethodInfo t9 = AccessTools.Method(typeof(Studio.MPCharCtrl), "OnClickRoot");
if (t9 != null)
{
    harmony.Patch(t9, prefix: new HarmonyMethod(AccessTools.Method(typeof(Patches), nameof(MPCharCtrlOnClickRootPrefix))));
    Log.LogInfo("パッチ適用: Studio.MPCharCtrl.OnClickRoot");
}
```

- [ ] **Step 2: Patch メソッドを追加**

```csharp
// v3.1.0: コスチュームタブ(_idx==4)で衣装モード開始、それ以外のタブ/閉じ(-1)で解除
// パネル非表示中の誤発火（Awake 内 OnClickRoot(select) 等）は activeInHierarchy でガード
private static void MPCharCtrlOnClickRootPrefix(Studio.MPCharCtrl __instance, int _idx)
{
    if (__instance == null || !__instance.gameObject.activeInHierarchy) return;
    if (_idx == 4)
    {
        SceneExplorerPlugin.CurrentBrowserMode = BrowserMode.Coordinate;
        SceneExplorerPlugin.CurrentBrowserFolder = SceneExplorerPlugin.GetModeRootFolder();
        SceneExplorerPlugin.Log.LogInfo("[SceneExplorer] Coordinateモード開始 folder=" + SceneExplorerPlugin.CurrentBrowserFolder);
    }
    else if (SceneExplorerPlugin.CurrentBrowserMode == BrowserMode.Coordinate)
    {
        SceneExplorerPlugin.RequestSceneMode("コスチュームタブ切替");
    }
}
```

注: `BrowserMode` はネスト enum でなく `SceneExplorerPlugin.BrowserMode` として定義（Task 1 Step 1 のまま）。Patches は `SceneExplorerPlugin` のネストクラスなので、`CurrentBrowserMode` 等はそのまま参照可。`BrowserMode.Coordinate` だけネーム解決が必要なら `SceneExplorerPlugin.BrowserMode.Coordinate` と書く。

- [ ] **Step 3: コスチュームタブのコンテンツ非表示（SceneBrowser.Update に追加）**

`SceneBrowser.cs` の Task 2 Step 3 で追加したブロックの直後に追加:

```csharp
// v3.1.0: 衣装モード中はコスチュームタブのコンテンツ（CostumeInfo）を非表示にして SceneBrowser に差し替える
if (SceneExplorerPlugin.CurrentBrowserMode == BrowserMode.Coordinate)
{
    var mp = UnityEngine.Object.FindObjectOfType<Studio.MPCharCtrl>();
    if (mp != null && mp.gameObject.activeInHierarchy)
    {
        // costumeInfo フィールド（private）のルート GameObject を非表示
        var fi = AccessTools.Field(typeof(Studio.MPCharCtrl), "costumeInfo");
        if (fi != null)
        {
            var ci = fi.GetValue(mp);
            if (ci != null)
            {
                var rootFi = AccessTools.Field(ci.GetType(), "objRoot") ?? AccessTools.Field(ci.GetType(), "root");
                if (rootFi != null)
                {
                    var go = rootFi.GetValue(ci) as UnityEngine.GameObject;
                    if (go != null && go.activeInHierarchy) go.SetActive(false);
                }
            }
        }
    }
    if (!_visible) _visible = true;
}
```

注: CostumeInfo のルートフィールド名（objRoot / root）は `obj\CostumeInfo_decompiled.cs` :27,64 で `objRoot.SetActive` / `root.SetActive` を確認済み。実装時に decompile を見て確実な方を使う（両方試す場合は `??` の順序で対応）。`AccessTools` の using は SceneExplorerPlugin.cs 側にあるため、SceneBrowser.cs では `HarmonyLib.AccessTools` と完全修飾するか、ファイル先頭に `using HarmonyLib;` を追加する（既に SceneTree.cs:5 で使用実績あり。SceneBrowser.cs に追加しても安全）。

- [ ] **Step 4: ビルド確認**

Run: `dotnet build`
Expected: exit 0。まだ SceneBrowser の表示拡張（Task 4）が無いため、この時点ではモード遷移のログのみ確認できる

- [ ] **Step 5: コミット**

```bash
git add SceneExplorerPlugin.cs SceneBrowser.cs
git commit -m "feat: hook MPCharCtrl.OnClickRoot to detect coordinate mode"
```

---

### Task 4: SceneBrowser の表示ロジックをモード対応に（表示条件・タイトル・ボトムバー）

**Files:**
- Modify: `SceneBrowser.cs`（ShouldBeVisible :558-560 / OnGUI :295 / DrawBottomBar）

**Interfaces:**
- Consumes: Task 1-3 の `CurrentBrowserMode` / `activeCharaList`
- Produces: モード別の表示条件・タイトル・ボトムバー表示

- [ ] **Step 1: ShouldBeVisible を拡張**

`SceneBrowser.cs:558-560` を変更:

```csharp
private bool ShouldBeVisible()
{
    // v3.1.0: シーンモードは activeLoadScene、キャラ/衣装モードは CurrentBrowserMode で判定
    if (SceneExplorerPlugin.CurrentBrowserMode != SceneExplorerPlugin.BrowserMode.Scene) return true;
    return SceneExplorerPlugin.activeLoadScene != null;
}
```

（:554 の同名チェックがある場合は同様に変更。OnGUI :295 の `shouldBeVisible = ShouldBeVisible() && !DialogSceneActive` はそのまま）

- [ ] **Step 2: タイトルをモード対応に**

OnGUI 内でウィンドウタイトル文字列を生成している箇所（:295-320 付近）を確認し、タイトル変数を以下で切替:

```csharp
string windowTitle;
switch (SceneExplorerPlugin.CurrentBrowserMode)
{
    case SceneExplorerPlugin.BrowserMode.CharaFemale: windowTitle = "キャラクターブラウザ（女）"; break;
    case SceneExplorerPlugin.BrowserMode.CharaMale:   windowTitle = "キャラクターブラウザ（男）"; break;
    case SceneExplorerPlugin.BrowserMode.Coordinate:  windowTitle = "衣装ブラウザ"; break;
    default: windowTitle = "シーンブラウザ"; break;
}
```

- [ ] **Step 3: ボトムバーのボタンをモード対応に**

`DrawBottomBar` 内の Import ボタン（`ImportSelected` 呼び出し部）と Delete ボタンを、Scene モード以外では表示しないよう `if (SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.Scene) { ... }` で囲む。Load ボタンのラベルはモードに応じて「Load」→「追加」に変更（ラベル文字列の変数化）。

- [ ] **Step 4: ビルド確認**

Run: `dotnet build`
Expected: exit 0

- [ ] **Step 5: コミット**

```bash
git add SceneBrowser.cs
git commit -m "feat: make SceneBrowser visibility/title/footer mode-aware"
```

---

### Task 5: ロード実行のモード分岐（キャラ追加・衣装適用）

**Files:**
- Modify: `SceneBrowser.cs`（LoadSelected :1258-1271 / LoadSceneRoutine :1273-1288）

**Interfaces:**
- Consumes: `CurrentBrowserMode` / SceneItem.FilePath
- Produces: モード別ロード実行（`LoadSelected` 内で分岐）

- [ ] **Step 1: LoadSelected にモード分岐を追加**

`SceneBrowser.cs:1261` の `var item = _items[_selectedIndex];` の後に追加:

```csharp
// v3.1.0: モード別ロード
switch (SceneExplorerPlugin.CurrentBrowserMode)
{
    case SceneExplorerPlugin.BrowserMode.CharaFemale:
        Studio.Studio.Instance.AddFemale(item.FilePath);
        CloseScene();
        return;
    case SceneExplorerPlugin.BrowserMode.CharaMale:
        Studio.Studio.Instance.AddMale(item.FilePath);
        CloseScene();
        return;
    case SceneExplorerPlugin.BrowserMode.Coordinate:
        ApplyCoordinate(item.FilePath);
        CloseScene();
        return;
}
```

（try/catch は既存の構造を維持し、例外時は Log.LogError を出して既存どおり）

- [ ] **Step 2: ApplyCoordinate メソッドを追加**（LoadSceneRoutine の直後など）

```csharp
// v3.1.0: 選択中キャラに衣装を適用（未選択なら何もしない）
private void ApplyCoordinate(string path)
{
    var targets = Studio.Studio.GetSelectObjectCtrl();
    foreach (var obj in targets)
    {
        if (obj is Studio.OCIChar oci)
        {
            SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] 衣装適用: " + path + " -> " + oci.treeNodeObject.name);
            oci.LoadClothesFile(path);
            return;
        }
    }
    SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] 衣装適用対象のキャラが選択されていません: " + path);
}
```

注: `Studio.Studio.GetSelectObjectCtrl()` は Studio_decompiled.cs:881 で public static 確認済み。`OCIChar` は `Studio.OCIChar`。複数選択時は最初の OCIChar に適用（標準 MPCharCtrl と同様、1体対象）。

- [ ] **Step 3: ビルド確認**

Run: `dotnet build`
Expected: exit 0

- [ ] **Step 4: コミット**

```bash
git add SceneBrowser.cs
git commit -m "feat: route load action by browser mode (add female/male, apply coordinate)"
```

---

### Task 6: ファイル検証と表示名（RescanFiles のモード別処理）

**Files:**
- Modify: `SceneBrowser.cs`（SceneItem クラス :163-173 / RescanFiles :1348-1400 付近）

**Interfaces:**
- Consumes: `CurrentBrowserMode` / `GetModeRootFolder`
- Produces: `SceneItem.DisplayName` / モード別のフィルタ（キャラは ChaFileControl 検証、衣装は ChaFileCoordinate 検証）

- [ ] **Step 1: SceneItem に DisplayName を追加**

`SceneBrowser.cs:166`（`public string FileName;` の直後）:

```csharp
public string DisplayName;   // v3.1.0: キャラ名/コーデ名表示用（Scene モードでは FileName と同値）
```

- [ ] **Step 2: RescanFiles のアイテム生成部をモード別に分岐**

RescanFiles 内で SceneItem を生成している箇所（`new SceneItem { FilePath = ..., FileName = ... }` 相当）を確認し、以下のヘルパーを呼ぶ形に変更。ヘルパーは RescanFiles の近くに追加:

```csharp
// v3.1.0: モード別のメタデータ検証。表示名を返す（検証失敗は null = 一覧に含めない）
private string ResolveDisplayName(string path)
{
    switch (SceneExplorerPlugin.CurrentBrowserMode)
    {
        case SceneExplorerPlugin.BrowserMode.CharaFemale:
        case SceneExplorerPlugin.BrowserMode.CharaMale:
        {
            int sex = (SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.CharaFemale) ? 1 : 0;
            try
            {
                var cf = new ChaFileControl();
                if (cf.LoadCharaFile(path, sex, noLoadPng: true)) return cf.parameter.fullname;
            }
            catch (Exception ex) { SceneExplorerPlugin.Log.LogWarning("キャラ検証失敗: " + path + ": " + ex.Message); }
            return null;
        }
        case SceneExplorerPlugin.BrowserMode.Coordinate:
        {
            try
            {
                var cc = new ChaFileCoordinate();
                if (cc.LoadFile(path)) return cc.coordinateName;
            }
            catch (Exception ex) { SceneExplorerPlugin.Log.LogWarning("コーデ検証失敗: " + path + ": " + ex.Message); }
            return null;
        }
        default:
            return System.IO.Path.GetFileName(path);
    }
}
```

- [ ] **Step 3: アイテム生成時に DisplayName を設定**

生成部で `DisplayName = ResolveDisplayName(path)` とし、**null の場合はそのアイテムを一覧に追加しない**（破損カードを除外。標準 CharaList と同一仕様）。`DisplayName` が null でない場合のみ `_items` に追加する形に変更。`SceneItem.FileName` はファイル名のまま残す（ツールチップ等で使用）。

- [ ] **Step 4: グリッド描画の表示名を DisplayName に変更**

DrawGridItem 内で `item.FileName` をラベル表示している箇所を `item.DisplayName` に変更（表示名が空でない場合は表示名優先）。

- [ ] **Step 5: ビルド確認**

Run: `dotnet build`
Expected: exit 0

- [ ] **Step 6: コミット**

```bash
git add SceneBrowser.cs
git commit -m "feat: validate chara/coordinate cards and show display names in browser"
```

---

### Task 7: モードルートフォルダの制限（ツリー/フォルダ移動のクランプ）

**Files:**
- Modify: `SceneBrowser.cs`（SelectFolder :1704-1707 / GetCurrentBrowserFolder :1781-1783 / ツリー描画）

**Interfaces:**
- Consumes: `GetModeRootFolder`
- Produces: モード中はルートフォルダより上へ移動できない制約

- [ ] **Step 1: モードルートを下回らないクランプ**

`SelectFolder`（:1704-1707、`SceneExplorerPlugin.CurrentBrowserFolder = path;`）の直前に追加:

```csharp
// v3.1.0: キャラ/衣装モードではモードルートより上へ移動させない
string modeRoot = SceneExplorerPlugin.GetModeRootFolder();
if (modeRoot != null)
{
    string rootFull = UserData.Path + modeRoot;
    if (!path.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        path = rootFull;
}
```

（`UserData` は `Manager.UserData`、SceneBrowser.cs の既存参照に依存。既存コードで使われている型に合わせる）

注: **既存の `CurrentBrowserFolder` の形式（フルパス or UserData 相対）を先に RescanFiles/SelectFolder の実装で確認すること。** フルパス形式なら上記の `UserData.Path + modeRoot` 連結でOK。相対形式なら `path.StartsWith(modeRoot, ...)` に変える。どちらの場合も比較は既存形式に統一する。

- [ ] **Step 2: モード開始時（Task 2/3 の Update 監視）にルートへ移動**

Task 2 Step 3 / Task 3 Step 3 で `_visible = true` を設定する箇所の直後に `RescanFiles();` を呼び、モードルートの一覧を読み直す（`CurrentBrowserFolder` は Task 1 の `RequestCharaMode` / Task 3 Step 2 で既に設定済み）。

- [ ] **Step 3: ビルド確認**

Run: `dotnet build`
Expected: exit 0

- [ ] **Step 4: コミット**

```bash
git add SceneBrowser.cs
git commit -m "feat: clamp browser folder navigation to mode root"
```

---

### Task 8: 総合ビルド・手動テスト・配置

**Files:**
- なし（検証のみ）

- [ ] **Step 1: フルビルド**

Run: `dotnet build`（Set-Location -LiteralPath 後に）
Expected: exit 0、0 エラー 0 警告

- [ ] **Step 2: DLL 配置**

Run: PowerShell で `Copy-Item -LiteralPath 'G:\MyGAME\Koikatsu\[MODDING] Tools\KK_SceneExplorer\bin\Debug\KK_SceneExplorer.dll' -Destination 'G:\MyGAME\Koikatsu\BepInEx\plugins\KK_SceneExplorer.dll' -Force`
Expected: コピー成功（Get-Item で時刻確認）

- [ ] **Step 3: 手動テスト手順（ユーザーへ報告）**

1. スタジオ起動 → メニューの「キャラ」→「女」タブを押す → 標準パネルの代わりに SceneBrowser（キャラクターブラウザ（女））が表示される
2. フォルダツリーで chara/female 配下を移動 → サムネが順次表示される（既存の非同期ロード）
3. カードを選択して「追加」→ スタジオにキャラが追加され、ブラウザが閉じる
4. 男タブでも同様（chara/male）
5. キャラを選択 → 操作パネルの「コスチューム」タブ → 衣装ブラウザが表示 → コーデを選択して「追加」→ 選択キャラの衣装が変更される
6. 破損 PNG が一覧に出ないこと（表示名 null 除外）
7. シーンブラウザが従来どおり動くこと（Load/Import/Delete/Close）
8. ブラウザ表示中、後ろの uGUI が操作できないこと（既存 EventSystem 制御）

- [ ] **Step 4: コミット（未コミット分があれば）とプッシュ**

```bash
git status
git push origin main
```

- [ ] **Step 5: README 更新（Drive-by）**

`README.md:63` の「v3.0.10 輝度デバッグ機能」記載を削除/更新（コード側は既に除去済み eb8fb9c）。v3.1.0 のキャラ/衣装ブラウザ機能の記載を追加。

```bash
git add README.md
git commit -m "docs: update README for chara/coordinate browser and remove stale brightness debug note"
```
