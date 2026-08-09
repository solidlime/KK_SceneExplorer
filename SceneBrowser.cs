using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Manager;
using UnityEngine;
using UnityEngine.UI;   // v3.3.1: 透明 uGUI ブロックレイヤー（Canvas/Image/GraphicRaycaster）

#pragma warning disable CS0414 // フィールド割当済みだが未使用

namespace KK_SceneExplorer
{
    /// <summary>
    /// 統合シーンブラウザ: 左ペインにフォルダツリー、右ペインにシーン一覧グリッド。
    /// SceneTree.cs の BF風描画流儀を踏襲。
    /// v2.2.0: スクロール式グリッド / ツールチップ位置修正 / フォント連動 / ウィンドウリサイズ＋記憶
    /// </summary>
    public class SceneBrowser : MonoBehaviour
    {
        // ── 定数 ──
        private const int WindowId = 981238;
        private const float CheckInterval = 0.3f;
        private const int IndentPerLevel = 12;
        private const int ToggleWidth = 18;
        private const int TreeLineWidth = 14;
        // v2.5.1: キャッシュ上限を引き上げ（50→300）。追い出されたアイテムは ThumbLoaded を
        // リセットして再読み込み可能にする（AddToThumbnailCache の追い出し処理と対）
        private const int MaxCacheSize = 300;
        private const float DeleteResetSeconds = 5f;
        private const float ItemPadX = 3f;   // v3.2.2: サムネ左右マージンを半分（6→3）に縮小
        private const float ItemPadY = 6f;
        private const float ItemGap = 4f;
        private const float ItemSpacing = 8f;
        private const float DateSize = 11f;
        private const float FilenameHeight = 18f;
        private const float ResizeHandleSize = 14f;
        private const float TooltipOffsetX = 16f;
        private const float TooltipOffsetY = 16f;
        private const float TooltipPad = 8f;
        private const float MinWindowWidth = 800f;
        private const float MinWindowHeight = 500f;

        // ── 動的レイアウト（フォントサイズ連動） ──
        // v2.5.3: TitleBarHeight 追加。ヘッダー/フッターの見切れ対策で
        // TextLineHeight（フォント実行高さ）を基準に統一。
        private float TitleBarHeight { get { return TextLineHeight + 8f; } }
        private float ToolbarHeight { get { return TextLineHeight + 8f; } }
        private const float BottomBarRightPadding = 24f; // 右下のリサイズエッジ/スクロールバーとの重なりを避ける余白
        private float BottomBarHeight { get { return TextLineHeight + 8f; } }
        private float SliderWidth { get { return Mathf.Max(100f, FontSizeVal * 7f); } }
        private float ButtonHeight { get { return TextLineHeight + 6f; } }
        private float TabButtonWidth { get { return Mathf.Max(72f, FontSizeVal * 5f); } }
        // v2.5.4: ボタン幅をTextLineHeight基準に変更（旧:固定乗算式）。フッター溢れ対策。
        private float SortButtonWidth { get { return TextLineHeight * 3f; } }
        private float ArrowButtonWidth { get { return TextLineHeight * 1.8f; } }
        private float PageNavButtonWidth { get { return Mathf.Max(28f, FontSizeVal * 2f); } }
        // v2.5.4: フッターボタン幅をFlexibleWidth化。固定幅プロパティ削除。
        private float FooterButtonWidth { get { return TextLineHeight * 3f; } }
        private float FontSizeVal { get { return (float)SceneExplorerPlugin.FontSize.Value; } }
        private float RowHeight { get { return Mathf.Max(16f, FontSizeVal + 2f); } }
        // v2.5.2: 日本語フォントの実際の行高（ascender+descender+leading）は
        // fontSize の約1.4倍。クリップ防止のためテキスト行の高さにこの係数を適用。
        private float TextLineHeight { get { return Mathf.Ceil(FontSizeVal * 1.4f); } }

        // ── 静的テクスチャ（Awakeで生成。GUI.skinには触らない）──
        private static Texture2D _selectedRowTex;
        private static Texture2D _hoverRowTex;
        private static Texture2D _splitterTex;
        private static Texture2D _selectedItemTex;
        private static Texture2D _hoverItemTex;
        private static Texture2D _emptyThumbTex;
        private static Texture2D _tooltipBgTex;
        private static Texture2D _resizeHandleTex;
        private static Texture2D _titleBarTex;
        private static Texture2D _windowBgTex;
        private static Texture2D _clearTex;
        private static Texture2D _scrollbarTrackTex;
        private static Texture2D _scrollbarThumbTex;

        // ── v2.0.6 GUIClipリーク対策 ──
        private static Type _clipType;
        private static PropertyInfo _clipVisibleRect;
        private static MethodInfo _clipPop;
        private static bool _clipWarned;

        // ── インスタンス状態 ──
        private Rect _windowRect;
        private bool _visible;
        // v3.3.1: 透明 uGUI ブロックレイヤー（ウィンドウ矩形に追従し、ウィンドウ内の背後 uGUI のみクリックをブロック。
        // ウィンドウ外はゲーム操作可能。旧 EventSystem ロック + IMGUI 吸収レイヤー方式から置き換え）
        private GameObject _blockLayer;
        private Canvas _blockCanvas;
        private RectTransform _blockRect;
        private bool _loading;
        private float _nextCheckTime;
        private bool _stylesReady;

        // v3.1.0: ブラウザモード遷移検出用（前回フレームのモード。遷移時のみ RescanFiles を1回実行する）
        private SceneExplorerPlugin.BrowserMode _lastMode = SceneExplorerPlugin.BrowserMode.Scene;
        // v3.1.0: 衣装モードのリフレクション結果キャッシュ（毎フレームの FindObjectOfType / AccessTools 解決を避ける）
        private Studio.MPCharCtrl _mpCharCtrl;
        private UnityEngine.GameObject _costumeRoot;
        private float _nextCostumeResolveTime;   // ResolveCostumeRoot の再試行間隔（解決失敗時の毎フレーム再試行を防ぐ）
        // v3.1.0: モード開始時に実際に非アクティブ化したパネルのスナップショット（復元対象。タブ選択状態と矛盾するパネルは復元しない）
        private readonly List<UnityEngine.GameObject> _hiddenModePanels = new List<UnityEngine.GameObject>();

        // ファイル一覧
        private List<SceneItem> _items = new List<SceneItem>();
        private int _selectedIndex = -1;
        private string _lastScannedFolder;

        // v3.4.0: メインゲーム保存モードの新規保存ファイル名（ボトムバーの TextField）
        private string _mainGameSaveFileName = "";

        // UI状態
        private Vector2 _treeScroll;
        private Vector2 _gridScroll;
        private float _splitPos = 240f;
        private bool _draggingSplitter;
        private float _thumbSize = 96f;
        private int _hoverItemIndex = -1;
        private string _tooltipText = "";
        private Rect _tooltipRect;
        private SortMode _sortMode = SortMode.Date;
        private bool _sortDescending = true;

        // ウィンドウリサイズ
        private bool _draggingResize;
        private Vector2 _resizeStartPos;
        private Rect _resizeStartRect;
        private float _lastSavedWidth;
        private float _lastSavedHeight;
        private float _saveDebounceTime;

        // デリート二段階確認
        private bool _deleteConfirm;
        private float _deleteConfirmTime;

        // ツリー状態
        private HashSet<string> _expandedFolders = new HashSet<string>();
        private string _treeFilter = "";
        private Dictionary<string, List<DirEntry>> _dirChildrenCache = new Dictionary<string, List<DirEntry>>();

        // サムネイルキャッシュ
        private Dictionary<string, Texture2D> _thumbCache = new Dictionary<string, Texture2D>();
        private List<string> _thumbCacheOrder = new List<string>();

        // ── 非同期サムネイルロード（net35: ThreadPool + lock キュー。ConcurrentQueue / Task / async は不使用）──
        // 要求キュー: メインスレッドが積み、バックグラウンドスレッドが取り出す
        private readonly object _thumbReqLock = new object();
        private readonly Queue<SceneItem> _thumbReqQueue = new Queue<SceneItem>();
        // 結果キュー: バックグラウンドスレッドが積み、メインスレッドが取り出す
        private readonly object _thumbResLock = new object();
        private readonly Queue<ThumbLoadResult> _thumbResQueue = new Queue<ThumbLoadResult>();

        // GUIStyle（OnGUI初回に生成）
        private GUIStyle _nodeButtonStyle;
        private GUIStyle _filterStyle;
        private GUIStyle _selectedItemStyle;
        private GUIStyle _dateStyle;
        private GUIStyle _toolbarButtonStyle;
        private GUIStyle _pageLabelStyle;
        private GUIStyle _tooltipStyle;
        private GUIStyle _splitterStyle;
        private GUIStyle _countLabelStyle;
        private GUIStyle _titleBarStyle;

        private enum SortMode
        {
            Name,
            Date,
            Size
        }


        private class SceneItem
        {
            public string FilePath;
            public string FileName;
            public string DisplayName;   // v3.1.0: キャラ名/コーデ名表示用（Scene モードでは FileName と同値）
            public DateTime LastWriteTime;
            public long FileSize;
            public Texture2D Thumbnail;
            public bool ThumbLoaded;
            // 非同期ロード要求済みフラグ（二重要求防止。GetThumbnail からのみ操作）
            public bool ThumbRequested;
        }

        // 非同期サムネイルロードの結果（バックグラウンド → メインスレッド受け渡し用）
        private class ThumbLoadResult
        {
            public SceneItem Item;
            public byte[] Data;
        }

        private class DirEntry
        {
            public string Name;
            public string FullPath;
            public bool HasChildren;
        }

        // ═══════════════════════════════════════════════════════
        // MonoBehaviour
        // ═══════════════════════════════════════════════════════

        private void Awake()
        {
            try
            {
            // テクスチャ生成（GUI.skinには触らない — SceneTree.csと同じ方式）
            _selectedRowTex = new Texture2D(1, 1);
            // v3.0.14: 表示時に ^2.2 変換されるため、設計色の ^(1/2.2) に事前補正（以下同様）
            _selectedRowTex.SetPixel(0, 0, new Color(0.523f, 0.716f, 0.953f, 0.65f));
            _selectedRowTex.Apply();

            _hoverRowTex = new Texture2D(1, 1);
            _hoverRowTex.SetPixel(0, 0, new Color(0.730f, 0.850f, 1.0f, 0.3f));
            _hoverRowTex.Apply();

            _splitterTex = new Texture2D(1, 1);
            _splitterTex.SetPixel(0, 0, new Color(0.621f, 0.659f, 0.730f, 1f));
            _splitterTex.Apply();

            _selectedItemTex = new Texture2D(1, 1);
            _selectedItemTex.SetPixel(0, 0, new Color(0.523f, 0.716f, 0.953f, 0.4f));
            _selectedItemTex.Apply();

            _hoverItemTex = new Texture2D(1, 1);
            _hoverItemTex.SetPixel(0, 0, new Color(0.730f, 0.850f, 1.0f, 0.2f));
            _hoverItemTex.Apply();

            _emptyThumbTex = new Texture2D(1, 1);
            _emptyThumbTex.SetPixel(0, 0, new Color(0.561f, 0.561f, 0.579f, 1f));
            _emptyThumbTex.Apply();

            // v3.0.15: サムネイル背景パネルは削除（不要になったため）

            _tooltipBgTex = new Texture2D(1, 1);
            _tooltipBgTex.SetPixel(0, 0, new Color(0.422f, 0.459f, 0.542f, 0.95f));
            _tooltipBgTex.Apply();

            _resizeHandleTex = new Texture2D(1, 1);
            _resizeHandleTex.SetPixel(0, 0, new Color(0.696f, 0.730f, 0.793f, 0.8f));
            _resizeHandleTex.Apply();

            // v2.5.3: カスタムタイトルバー背景（Unity標準タイトルバーの代わりに描画）
            _titleBarTex = new Texture2D(1, 1);
            _titleBarTex.SetPixel(0, 0, new Color(0.435f, 0.481f, 0.581f, 1f));
            _titleBarTex.Apply();

            // v3.0.5: ウィンドウ背景（明るいダークブルーグレー。青み維持＋明度アップ）
            _windowBgTex = new Texture2D(1, 1);
            _windowBgTex.SetPixel(0, 0, new Color(0.503f, 0.542f, 0.629f, 0.94f));
            _windowBgTex.Apply();

            // v2.6.0: モーダル用透明テクスチャ（クリック吸収レイヤーに使用）
            _clearTex = new Texture2D(1, 1);
            _clearTex.SetPixel(0, 0, new Color(0, 0, 0, 0));
            _clearTex.Apply();

            // v3.0.3: スクロールバー用テクスチャ（背景と同化しないよう明るめに）
            _scrollbarTrackTex = new Texture2D(1, 1);
            _scrollbarTrackTex.SetPixel(0, 0, new Color(0.422f, 0.422f, 0.437f, 1f));
            _scrollbarTrackTex.Apply();

            _scrollbarThumbTex = new Texture2D(1, 1);
            _scrollbarThumbTex.SetPixel(0, 0, new Color(0.659f, 0.696f, 0.762f, 1f));
            _scrollbarThumbTex.Apply();

            // 保存済みウィンドウサイズを読み込み
            _lastSavedWidth = (float)SceneExplorerPlugin.BrowserWidth.Value;
            _lastSavedHeight = (float)SceneExplorerPlugin.BrowserHeight.Value;
            // v3.0.2: 保存済みサムネイルサイズを読み込み（Plugin.Awake の Config.Bind より後に実行されるため安全）
            _thumbSize = (float)SceneExplorerPlugin.ThumbSize.Value;
            // 保存済みスプリッター位置を読み込み（同上の理由で Plugin.Awake より後に実行）
            _splitPos = (float)SceneExplorerPlugin.TreeSplitPos.Value;

            // v3.2.1: 保存済みソート状態を復元（enum 範囲外なら Date に矯正）
            int savedSort = SceneExplorerPlugin.SortMode.Value;
            if (savedSort < 0 || savedSort > 2) _sortMode = SortMode.Date;
            else _sortMode = (SortMode)savedSort;
            _sortDescending = SceneExplorerPlugin.SortDescending.Value;

            // v3.2.1: 最後に開いたシーンフォルダを復元（存在しないパスは無視 = ローカルルートのまま）
            // v3.2.2: 共通メソッド化（ブラウザ再表示時にも復元されるように CheckFolderChanged からも呼ぶ）
            TryRestoreLastFolder();

            // v3.3.1: 透明 uGUI ブロックレイヤーを生成（ブラウザ非表示中は Canvas 無効のまま）
            EnsureBlockLayer();
            }
            catch (Exception ex)
            {
                // v3.3.0: Awake 内で例外が発生してもコンポーネントを無効化させない（CatchUnityEventExceptions 対策）
                // 設定値の不正（バックスラッシュのエスケープ解釈による制御文字混入など）は自己修復される
                SceneExplorerPlugin.Log.LogError("SceneBrowser Awake で例外が発生しました: " + ex);
            }
        }

        // v3.2.2: シーンモードで CurrentBrowserFolder が未設定なら保存済みフォルダを復元する
        // （モード切替で null にリセットされた後やブラウザを開き直したときにローカルルートへ戻ってしまう問題の修正）
        // v3.3.0: BepInEx 設定のエスケープ解釈（\n 等）で制御文字が混入した不正値で
        // OnGUI が例外停止しないよう防御。不正値は警告して空に自己修復する。
        private void TryRestoreLastFolder()
        {
            try
            {
                if (SceneExplorerPlugin.CurrentBrowserMode != SceneExplorerPlugin.BrowserMode.Scene) return;
                if (SceneExplorerPlugin.CurrentBrowserFolder != null) return;
                string last = SceneExplorerPlugin.LastFolder.Value;
                if (string.IsNullOrEmpty(last)) return;
                if (last.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                {
                    SceneExplorerPlugin.Log.LogWarning("保存済みフォルダの設定値に不正な文字が含まれます（LastFolder をリセットしました）");
                    SceneExplorerPlugin.LastFolder.Value = "";
                    SceneExplorerPlugin.ConfigFile.Save();
                    return;
                }
                if (!System.IO.Path.IsPathRooted(last)) last = System.IO.Path.Combine(UserData.Path, last);
                if (System.IO.Directory.Exists(last)) SceneExplorerPlugin.CurrentBrowserFolder = last;
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("保存済みフォルダの復元に失敗したため設定をリセットしました: " + ex.Message);
                SceneExplorerPlugin.LastFolder.Value = "";
                SceneExplorerPlugin.ConfigFile.Save();
            }
        }

        private void Update()
        {
            // 非同期サムネイルロードの結果をメインスレッドで処理（1フレーム最大2件）
            ProcessThumbnailResults();

            // v3.3.1: 透明 uGUI ブロックレイヤーをウィンドウ矩形に追従させる。
            // ウィンドウ内の背後 uGUI のみクリックブロック（ウィンドウ外はゲーム操作可能）。
            UpdateBlockLayer();

            // v3.1.0: ブラウザモード遷移の検出と1回限りの初期化
            // RequestCharaMode / MPCharCtrlOnClickRootPrefix 側で CurrentBrowserMode が切り替わるため、
            // ここでは遷移時にのみ RescanFiles（ルート読込）と標準パネルの復元を行う。毎フレームの
            // RescanFiles は行わない（衣装モードで数百ファイルのパースが毎フレーム走る問題の修正）。
            if (SceneExplorerPlugin.CurrentBrowserMode != _lastMode)
            {
                _lastMode = SceneExplorerPlugin.CurrentBrowserMode;
                if (_lastMode == SceneExplorerPlugin.BrowserMode.Scene)
                {
                    // モード解除: 標準パネルを復元
                    RestoreStandardPanels();
                }
                else
                {
                    // モード開始: 標準パネルを隠してモードルートを読み直す
                    HideModePanels();
                    if (!_visible) _visible = true;
                    RescanFiles();
                }
            }

            // v3.1.0: モード中は標準パネルの再表示を毎フレーム抑止（軽量な維持監視のみ。RescanFiles は呼ばない）
            bool wantChara = SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.CharaFemale ||
                             SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.CharaMale;
            if (wantChara)
            {
                foreach (var cl in SceneExplorerPlugin.activeCharaLists)
                {
                    if (cl != null && cl.gameObject.activeInHierarchy) cl.gameObject.SetActive(false);
                }
            }
            else if (SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.Coordinate)
            {
                // objRoot の非表示は MPCharCtrlOnClickRootPrefix で実施済み。ここはキャッシュ参照の維持確認のみ
                // （解決失敗時は毎フレーム再試行せず 0.5 秒間隔で再解決）
                if (_costumeRoot == null && Time.time >= _nextCostumeResolveTime)
                {
                    _costumeRoot = ResolveCostumeRoot();
                    _nextCostumeResolveTime = Time.time + 0.5f;
                }
                if (_costumeRoot != null && _costumeRoot.activeInHierarchy) _costumeRoot.SetActive(false);
            }

            if (_loading) return;
            if (Time.time < _nextCheckTime) return;
            _nextCheckTime = Time.time + CheckInterval;

            bool shouldBeVisible = ShouldBeVisible() && !SceneExplorerPlugin.DialogSceneActive;
            if (shouldBeVisible != _visible)
            {
                _visible = shouldBeVisible;
                if (_visible)
                {
                    CenterWindow();
                    RescanFiles();
                }
                else
                {
                    _selectedIndex = -1;
                    _tooltipText = "";
                    SaveWindowSize();
                }
            }
        }

        // v3.3.1: 透明 uGUI ブロックレイヤーの生成。
        // ウィンドウ矩形に追従する ScreenSpaceOverlay の透明 Image で、ウィンドウ内の背後 uGUI のみ
        // クリックをブロックする（ウィンドウ外は EventSystem を解放し、ゲーム操作を可能にする）。
        // CanvasScaler は付けない（scaleFactor=1 → ピクセル座標 = Canvas 座標で扱える）。
        private void EnsureBlockLayer()
        {
            if (_blockCanvas != null) return;
            try
            {
                var go = new GameObject("SceneExplorerModalBlock");
                go.transform.SetParent(transform, false);
                _blockLayer = go;
                _blockCanvas = go.AddComponent<Canvas>();
                _blockCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _blockCanvas.sortingOrder = 9999;   // スタジオUIの最前面に重ねる
                _blockCanvas.enabled = false;       // ブラウザ非表示中はブロックしない
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

        // v3.3.1: ブロックレイヤーをウィンドウ矩形に追従させる。
        // IMGUI は左上原点、uGUI ScreenSpaceOverlay は左下原点なので y を反転する。
        private void UpdateBlockLayer()
        {
            if (_blockCanvas == null) return;
            if (_visible)
            {
                if (!_blockCanvas.enabled) _blockCanvas.enabled = true;
                float x = _windowRect.x;
                float y = _windowRect.y;
                float w = _windowRect.width;
                float h = _windowRect.height;
                _blockRect.anchorMin = Vector2.zero;
                _blockRect.anchorMax = Vector2.zero;
                _blockRect.pivot = Vector2.zero;
                // 親 Canvas（全画面・左下原点）に対する相対座標で指定する
                _blockRect.anchoredPosition = new Vector2(x, Screen.height - (y + h));
                _blockRect.sizeDelta = new Vector2(w, h);
            }
            else if (_blockCanvas.enabled)
            {
                _blockCanvas.enabled = false;
            }
        }

        private void OnGUI()
        {
            // v2.1.1: 終了確認・確認ダイアログ（StudioExit/StudioCheck）表示中は
            // 描画もマウスイベントも完全スキップ（uGUIの確認ボタンのクリックを奪わない）。
            if (SceneExplorerPlugin.DialogSceneActive)
            {
                if (_visible)
                {
                    _visible = false;
                }
                return;
            }

            if (!_visible) return;

            // Ctrl+ホイールでサムネサイズ変更（ツールバーのスライダーと同範囲・同保存）
            // ウィンドウ内にマウスがある場合のみ反応（スタジオ側の Ctrl+ホイール操作と衝突しない）
            if (Event.current.type == EventType.ScrollWheel && Event.current.control &&
                _windowRect.Contains(Event.current.mousePosition))
            {
                float step = (Event.current.delta.y < 0f) ? 40f : -40f;  // v3.0.16: 拡縮率を5倍（8→40）に増加
                _thumbSize = Mathf.Clamp(_thumbSize + step, 48f, 600f);
                SceneExplorerPlugin.ThumbSize.Value = (int)_thumbSize;
                Event.current.Use();
            }

            // 他プラグイン（Skin Overlay Mod等）がGUI.matrixに残した変換を強制リセット。
            // これを行わないとスケール・オフセットが掛かり、ウィンドウが左上の小領域に縮小描画される。
            // SettingsUi.csは短小ウィンドウで影響が小さいため非顕在化しているが、原理は同じ。
            GUI.matrix = Matrix4x4.identity;

            // v2.0.6: 他プラグインがPopし忘れたGUIClipスタックを剥がす（クリップリーク対策）
            ResetClipLeak();

            // v3.3.1: IMGUI 吸収レイヤーは撤去（ウィンドウ外もゲーム操作可能にするため）。
            // ウィンドウ内の背後 uGUI のブロックは透明 uGUI ブロックレイヤー（UpdateBlockLayer）が担う。

            // 最前面に描画（他プラグインのIMGUIウィンドウに覆われないように。GUI.depthは小さいほど前面）
            GUI.depth = -1000;

            InitStylesOnce();

            // ウィンドウドラッグ
            // v2.0.7: GUI.Window に変更（GUILayout.Window はレイアウト計算（LayoutSingleGroup→Internal_MoveWindow）が
            // ネイティブのウィンドウRectを上書きして MinWindowWidth/Height へ縮小するため、レイアウト計算を根本回避）。
            // GUI.Window は clientRect をそのまま使用し、ドラッグはネイティブ標準で機能する（返り値がドラッグ後Rect）。
            // v2.5.3: 空タイトルで標準タイトルバーを非表示化。カスタムヘッダーは DrawWindow 内で描画。
            Rect winResult = GUI.Window(WindowId, _windowRect, DrawWindow, "");
            _windowRect.x = winResult.x;
            _windowRect.y = winResult.y;

            // リサイズハンドル処理（ウィンドウ外のイベントなのでここで処理）
            HandleWindowResize();

            ConstrainWindow();

            // ツールチップ描画（マウス追従＋画面端クランプ）
            if (!string.IsNullOrEmpty(_tooltipText) && Event.current.type == EventType.Repaint)
            {
                DrawTooltip();
            }

            // サイズ変化を遅延保存（ドラッグ中の頻繁なI/Oを避ける）
            if (_windowRect.width != _lastSavedWidth || _windowRect.height != _lastSavedHeight)
            {
                if (Time.time > _saveDebounceTime)
                {
                    SaveWindowSize();
                }
            }
        }

        // v2.2.0: ウィンドウ右下ドラッグでリサイズ
        private void HandleWindowResize()
        {
            var e = Event.current;
            var resizeRect = new Rect(
                _windowRect.xMax - ResizeHandleSize,
                _windowRect.yMax - ResizeHandleSize,
                ResizeHandleSize,
                ResizeHandleSize);

            if (e.type == EventType.MouseDown && resizeRect.Contains(e.mousePosition))
            {
                _draggingResize = true;
                _resizeStartPos = e.mousePosition;
                _resizeStartRect = _windowRect;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _draggingResize)
            {
                float newW = _resizeStartRect.width + (e.mousePosition.x - _resizeStartPos.x);
                float newH = _resizeStartRect.height + (e.mousePosition.y - _resizeStartPos.y);
                float screenW = Screen.width - 20f;
                float screenH = Screen.height - 20f;
                _windowRect.width = Mathf.Clamp(newW, Mathf.Min(MinWindowWidth, screenW), screenW);
                _windowRect.height = Mathf.Clamp(newH, Mathf.Min(MinWindowHeight, screenH), screenH);
                _saveDebounceTime = Time.time + 0.5f;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _draggingResize)
            {
                _draggingResize = false;
                SaveWindowSize();
                e.Use();
            }

            // カーカル変更（リサイズ領域にマウスが来たらサイズ変更カーソルに）
            if (resizeRect.Contains(e.mousePosition) && !_draggingSplitter)
            {
                // IMGUIではCursorの変更が直接できないため、ツールチップでリサイズ可能を示す
                _tooltipText = "\u30ea\u30b5\u30a4\u30ba"; // リサイズ
                _tooltipRect = new Rect(e.mousePosition.x + TooltipOffsetX, e.mousePosition.y + TooltipOffsetY, 60, 20);
            }
        }

        private void SaveWindowSize()
        {
            int w = Mathf.RoundToInt(_windowRect.width);
            int h = Mathf.RoundToInt(_windowRect.height);
            if (w != _lastSavedWidth || h != _lastSavedHeight)
            {
                _lastSavedWidth = w;
                _lastSavedHeight = h;
                SceneExplorerPlugin.BrowserWidth.Value = w;
                SceneExplorerPlugin.BrowserHeight.Value = h;
            }
        }

        // v2.0.6: GUIClipリーク対策。UnityEngine.GUIClipはinternalクラスなのでリフレクションでアクセスする。
        // visibleRect（public）が画面全体未満ならスタックが積まれてる＝Popを上限ガード付きで剥がす。
        private static void ResetClipLeak()
        {
            try
            {
                if (_clipType == null)
                {
                    _clipType = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip");
                    if (_clipType == null) return;
                    _clipVisibleRect = _clipType.GetProperty("visibleRect", BindingFlags.Static | BindingFlags.Public);
                    _clipPop = _clipType.GetMethod("Pop", BindingFlags.Static | BindingFlags.NonPublic);
                    if (_clipVisibleRect == null || _clipPop == null) return;
                }
                Rect visible = (Rect)_clipVisibleRect.GetValue(null, null);
                bool clipped = visible.width < Screen.width - 0.5f || visible.height < Screen.height - 0.5f;
                if (!clipped) return;
                int guard = 0;
                string before = visible.x + "," + visible.y + "," + visible.width + "x" + visible.height;
                while (guard++ < 64)
                {
                    try
                    {
                        _clipPop.Invoke(null, null);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                    visible = (Rect)_clipVisibleRect.GetValue(null, null);
                    if (visible.width >= Screen.width - 0.5f && visible.height >= Screen.height - 0.5f) break;
                }
                if (!_clipWarned)
                {
                    _clipWarned = true;
                    visible = (Rect)_clipVisibleRect.GetValue(null, null);
                    SceneExplorerPlugin.Log.LogInfo("クリップリークをリセットしました: " + before + " → " + visible.x + "," + visible.y + "," + visible.width + "x" + visible.height);
                }
            }
            catch (Exception)
            {
                // 診断不能な環境では黙って無視（描画は継続）
            }
        }

        private void OnDestroy()
        {
            // v3.3.1: 透明 uGUI ブロックレイヤーを破棄
            if (_blockLayer != null) Destroy(_blockLayer);
            _blockCanvas = null;
            _blockRect = null;
            if (_selectedRowTex != null) Destroy(_selectedRowTex);
            if (_hoverRowTex != null) Destroy(_hoverRowTex);
            if (_splitterTex != null) Destroy(_splitterTex);
            if (_selectedItemTex != null) Destroy(_selectedItemTex);
            if (_hoverItemTex != null) Destroy(_hoverItemTex);
            if (_emptyThumbTex != null) Destroy(_emptyThumbTex);
            if (_tooltipBgTex != null) Destroy(_tooltipBgTex);
            if (_resizeHandleTex != null) Destroy(_resizeHandleTex);
            if (_titleBarTex != null) Destroy(_titleBarTex);
            if (_windowBgTex != null) Destroy(_windowBgTex);
            if (_clearTex != null) Destroy(_clearTex);
            if (_scrollbarTrackTex != null) Destroy(_scrollbarTrackTex);
            if (_scrollbarThumbTex != null) Destroy(_scrollbarThumbTex);
            ClearThumbnailCache();
            SaveWindowSize();
        }

        // ═══════════════════════════════════════════════════════
        // 可視判定
        // ═══════════════════════════════════════════════════════

        private static bool IsLoadSceneVisible()
        {
            return SceneExplorerPlugin.activeLoadScene != null;
        }

        // シーン一覧ダイアログ（SceneLoadScene）が存在する間のみ表示
        // v3.1.0: シーンモードは activeLoadScene、キャラ/衣装モードは CurrentBrowserMode で判定
        private bool ShouldBeVisible()
        {
            if (SceneExplorerPlugin.CurrentBrowserMode != SceneExplorerPlugin.BrowserMode.Scene) return true;
            return SceneExplorerPlugin.activeLoadScene != null;
        }

        // ═══════════════════════════════════════════════════════
        // スタイル初期化（OnGUI初回のみ）
        // ═══════════════════════════════════════════════════════

        private void InitStylesOnce()
        {
            if (_stylesReady) return;

            var skin = GUI.skin;
            int fs = SceneExplorerPlugin.FontSize.Value;

            _nodeButtonStyle = new GUIStyle(skin.label);
            _nodeButtonStyle.normal.textColor = new Color(0.88f, 0.89f, 0.92f);
            _nodeButtonStyle.onNormal.background = _selectedRowTex;
            _nodeButtonStyle.onNormal.textColor = Color.white;
            _nodeButtonStyle.hover.textColor = new Color(0.55f, 0.75f, 1.0f);
            _nodeButtonStyle.focused.textColor = new Color(0.55f, 0.75f, 1.0f);
            _nodeButtonStyle.fontSize = fs;
            _nodeButtonStyle.alignment = TextAnchor.MiddleLeft;
            _nodeButtonStyle.padding = new RectOffset(4, 4, 2, 2);
            _nodeButtonStyle.richText = true;

            _filterStyle = new GUIStyle(skin.textField);
            _filterStyle.fontSize = fs;

            _selectedItemStyle = new GUIStyle(skin.label);
            _selectedItemStyle.normal.textColor = Color.white;
            _selectedItemStyle.alignment = TextAnchor.UpperCenter;
            _selectedItemStyle.wordWrap = true;
            _selectedItemStyle.fontSize = fs;
            _selectedItemStyle.padding = new RectOffset(2, 2, 0, 0);

            _dateStyle = new GUIStyle(skin.label);
            _dateStyle.normal.textColor = new Color(0.72f, 0.74f, 0.78f);
            _dateStyle.fontSize = fs;
            _dateStyle.alignment = TextAnchor.UpperCenter;

            _toolbarButtonStyle = new GUIStyle(skin.button);
            _toolbarButtonStyle.fontSize = fs;
            _toolbarButtonStyle.padding = new RectOffset(8, 8, 4, 4);
            _toolbarButtonStyle.fixedHeight = ButtonHeight;

            _pageLabelStyle = new GUIStyle(skin.label);
            _pageLabelStyle.normal.textColor = new Color(0.88f, 0.89f, 0.92f);
            _pageLabelStyle.alignment = TextAnchor.MiddleCenter;
            _pageLabelStyle.fontSize = fs;

            _tooltipStyle = new GUIStyle(skin.label);
            _tooltipStyle.normal.background = _tooltipBgTex;
            _tooltipStyle.normal.textColor = new Color(0.95f, 0.95f, 0.95f, 1f);
            _tooltipStyle.fontSize = fs;
            _tooltipStyle.padding = new RectOffset(6, 6, 4, 4);
            _tooltipStyle.wordWrap = false;

            _splitterStyle = new GUIStyle();
            _splitterStyle.normal.background = _splitterTex;

            _countLabelStyle = new GUIStyle(skin.label);
            _countLabelStyle.normal.textColor = new Color(0.72f, 0.74f, 0.78f);
            _countLabelStyle.alignment = TextAnchor.MiddleLeft;
            _countLabelStyle.fontSize = fs;

            // v2.5.3: カスタムタイトルバースタイル
            _titleBarStyle = new GUIStyle(skin.label);
            _titleBarStyle.normal.background = _titleBarTex;
            _titleBarStyle.normal.textColor = new Color(0.88f, 0.89f, 0.92f);
            _titleBarStyle.fontSize = fs;
            _titleBarStyle.alignment = TextAnchor.MiddleLeft;
            _titleBarStyle.padding = new RectOffset(8, 8, 4, 4);

            _stylesReady = true;
        }

        /// <summary>フォントサイズ変更を反映するため、次回OnGUIでスタイルを再生成させる。</summary>
        public void RefreshStyles()
        {
            _stylesReady = false;
        }

        // ═══════════════════════════════════════════════════════
        // メインウィンドウ描画
        // ═══════════════════════════════════════════════════════

        private void DrawWindow(int id)
        {
            _tooltipText = "";

            // v2.6.0: ウィンドウ背景をほぼ不透明で描画（Unity標準の透過背景を上書き）
            var fullRect = new Rect(0, 0, _windowRect.width, _windowRect.height);
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(fullRect, _windowBgTex, ScaleMode.StretchToFill);
            }

            // 1) フォルダが変更されていたら再スキャン
            CheckFolderChanged();

            // v2.5.3: カスタムタイトルバー（標準タイトルバーの見切れ対策）
            var titleRect = new Rect(0, 0, fullRect.width, TitleBarHeight);
            var toolbarRect = new Rect(0, titleRect.yMax, fullRect.width, ToolbarHeight + 4);
            var contentRect = new Rect(0, toolbarRect.yMax, fullRect.width, fullRect.height - toolbarRect.yMax);
            var bottomRect = new Rect(0, contentRect.yMax - BottomBarHeight, fullRect.width, BottomBarHeight);
            var bodyRect = new Rect(contentRect.x, contentRect.y, contentRect.width, contentRect.height - BottomBarHeight);

            // v3.1.0: タイトルをモード別表示
            string windowTitle;
            switch (SceneExplorerPlugin.CurrentBrowserMode)
            {
                case SceneExplorerPlugin.BrowserMode.CharaFemale: windowTitle = "\u30ad\u30e3\u30e9\u30af\u30bf\u30fc\u30d6\u30e9\u30a6\u30b6\uff08\u5973\uff09"; break; // キャラクターブラウザ（女）
                case SceneExplorerPlugin.BrowserMode.CharaMale:   windowTitle = "\u30ad\u30e3\u30e9\u30af\u30bf\u30fc\u30d6\u30e9\u30a6\u30b6\uff08\u7537\uff09"; break; // キャラクターブラウザ（男）
                case SceneExplorerPlugin.BrowserMode.Coordinate:  windowTitle = "\u8863\u88c5\u30d6\u30e9\u30a6\u30b6"; break; // 衣装ブラウザ
                default: windowTitle = "\u30b7\u30fc\u30f3\u30d6\u30e9\u30a6\u30b6"; break; // シーンブラウザ
            }

            // タイトルバー描画
            if (Event.current.type == EventType.Repaint)
            {
                _titleBarStyle.Draw(titleRect, "\u2601 " + windowTitle, false, false, false, false); // ☁ + モード別タイトル
            }

            DrawToolbar(toolbarRect);
            DrawSplitContent(bodyRect);
            DrawBottomBar(bottomRect);

            // リサイズハンドル（右下）
            if (Event.current.type == EventType.Repaint)
            {
                var rh = new Rect(fullRect.xMax - ResizeHandleSize, fullRect.yMax - ResizeHandleSize, ResizeHandleSize, ResizeHandleSize);
                GUI.DrawTexture(rh, _resizeHandleTex);
                // 三角形風のインジケータ
                GUI.color = new Color(0.55f, 0.60f, 0.68f);
                for (int i = 0; i < 4; i++)
                {
                    float ofs = 2 + i * 3;
                    GUI.DrawTexture(new Rect(rh.xMax - ofs, rh.yMax - 1, 1, -(ofs)), _splitterTex);
                }
                GUI.color = Color.white;
            }

            // ドラッグ領域: タイトルバー全体
            GUI.DragWindow(titleRect);
        }


        // ── ツールバー ──
        private void DrawToolbar(Rect rect)
        {
            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal();

            // ソートボタン
            GUI.backgroundColor = _sortMode == SortMode.Name ? new Color(0.3f, 0.5f, 0.9f) : Color.white;
            if (GUILayout.Button("\u540d\u524d", _toolbarButtonStyle, GUILayout.Width(SortButtonWidth))) ToggleSort(SortMode.Name); // 名前
            GUI.backgroundColor = _sortMode == SortMode.Date ? new Color(0.3f, 0.5f, 0.9f) : Color.white;
            if (GUILayout.Button("\u65e5\u6642", _toolbarButtonStyle, GUILayout.Width(SortButtonWidth))) ToggleSort(SortMode.Date); // 日時
            GUI.backgroundColor = _sortMode == SortMode.Size ? new Color(0.3f, 0.5f, 0.9f) : Color.white;
            if (GUILayout.Button("Size", _toolbarButtonStyle, GUILayout.Width(SortButtonWidth))) ToggleSort(SortMode.Size);
            GUI.backgroundColor = Color.white;

            // 昇順/降順トグル
            string arrow = _sortDescending ? "\u25bc" : "\u25b2"; // ▼ ▲
            if (GUILayout.Button(arrow, _toolbarButtonStyle, GUILayout.Width(ArrowButtonWidth)))
            {
                _sortDescending = !_sortDescending;
                SortItems();
            }

            GUILayout.FlexibleSpace();

            // サムネサイズスライダー（v3.0.2: 変更を設定に書き戻し、次回起動時に復元）
            GUILayout.Label("\u30b5\u30e0\u30cd:", GUILayout.Width(FontSliderLabelWidth)); // サムネ:
            float newThumb = GUILayout.HorizontalSlider(_thumbSize, 48f, 600f, GUILayout.Width(SliderWidth));
            if (Mathf.Abs(newThumb - _thumbSize) > 0.5f)
            {
                _thumbSize = newThumb;
                SceneExplorerPlugin.ThumbSize.Value = (int)newThumb;
            }
            GUILayout.Label(((int)_thumbSize).ToString() + "px", GUILayout.Width(FontSliderPxWidth));

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // フォント連動のラベル幅
        private float FontSliderLabelWidth { get { return Mathf.Max(50f, FontSizeVal * 3.5f); } }
        private float FontSliderPxWidth { get { return Mathf.Max(35f, FontSizeVal * 2.5f); } }

        // ── 分割ペイン描画 ──
        private void DrawSplitContent(Rect body)
        {
            // スプリッターのドラッグ処理
            var splitterRect = new Rect(body.x + _splitPos, body.y, 6f, body.height);
            HandleSplitterDrag(splitterRect, body);

            // 左: ツリーペイン
            var treeRect = new Rect(body.x, body.y, _splitPos - 3f, body.height);
            DrawTreePanel(treeRect);

            // スプリッター描画
            if (Event.current.type == EventType.Repaint)
            {
                GUI.skin.box.Draw(splitterRect, false, false, false, false);
            }

            // 右: グリッドペイン
            float gridX = splitterRect.xMax;
            float gridW = body.xMax - gridX;
            var gridRect = new Rect(gridX, body.y, gridW, body.height);
            DrawGridPanel(gridRect);
        }

        // ── スプリッタードラッグ ──
        private void HandleSplitterDrag(Rect splitterRect, Rect body)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && splitterRect.Contains(e.mousePosition))
            {
                _draggingSplitter = true;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _draggingSplitter)
            {
                _splitPos = Mathf.Clamp(e.mousePosition.x - body.x, 120f, body.width - 120f);
                e.Use();
                GUI.changed = true;
            }
            else if (e.type == EventType.MouseUp && _draggingSplitter)
            {
                _draggingSplitter = false;
                // ドラッグ終了時にのみ保存（ドラッグ中の毎フレーム保存はしない）
                SceneExplorerPlugin.TreeSplitPos.Value = _splitPos;
                e.Use();
            }
        }

        // ── ボトムバー（v2.2.0: ページング廃止、全件表示） ──
        private void DrawBottomBar(Rect rect)
        {
            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal();

            // 全件数表示
            GUILayout.Label("All " + _items.Count.ToString(), _countLabelStyle, GUILayout.Width(CountLabelWidth));

            GUILayout.FlexibleSpace();

            bool charaMode = SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.CharaFemale ||
                             SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.CharaMale;

            // v3.2.1: キャラモードは [Add][Replace] の明示分離（Add = 常に追加 / Replace = 常に置き換え）
            if (charaMode)
            {
                // v3.4.0: メインゲームのキャラ保存モード（CharaSave）では標準の保存 UI を隠しているため、
                // [Add/Replace/Keep Clothes] をファイル名入力 + [Save New] + [Overwrite] に差し替える
                if (SceneExplorerPlugin.IsMainGameCharaSaveMode)
                {
                    _mainGameSaveFileName = GUILayout.TextField(_mainGameSaveFileName, GUILayout.MinWidth(FooterButtonWidth * 2f));

                    GUI.enabled = _mainGameSaveFileName.Trim().Length > 0;
                    if (GUILayout.Button("Save New", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                    {
                        SaveCharaInMainGame(BuildMainGameSavePath(_mainGameSaveFileName), false);
                    }
                    GUI.enabled = _selectedIndex >= 0;
                    if (GUILayout.Button("Overwrite", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                    {
                        if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
                            SaveCharaInMainGame(_items[_selectedIndex].FilePath, true);
                    }
                    GUI.enabled = true;
                }
                else
                {
                    int sex = (SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.CharaFemale) ? 1 : 0;

                    GUI.enabled = _selectedIndex >= 0;
                    if (GUILayout.Button("Add", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                    {
                        if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
                            AddSelected(_items[_selectedIndex].FilePath, sex);
                    }
                    GUI.enabled = true;

                    // Replace は選択オブジェクトが無いとき無効化（OCIChar 判定はクリック時。Count のみ毎フレーム判定）
                    var gom = Singleton<Studio.GuideObjectManager>.Instance;
                    bool hasSelection = gom != null && gom.selectObjectKey != null && gom.selectObjectKey.Count() > 0;
                    GUI.enabled = _selectedIndex >= 0 && hasSelection;
                    if (GUILayout.Button("Replace", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                    {
                        if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
                            ReplaceSelected(_items[_selectedIndex].FilePath, sex);
                    }
                    // v3.2.3: 服を変えずに顔・体型・髪だけ差し替える（標準 ChangeChara は服も変わるため）
                    if (GUILayout.Button("Keep Clothes", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                    {
                        if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
                            KeepClothesSelected(_items[_selectedIndex].FilePath, sex);
                    }
                    GUI.enabled = true;
                }
            }
            else
            {
                // v2.5.4: ボタン幅をFlexibleWidth化（ウィンドウ幅に応じて均等スケール）。ラベル英語化。
                // v3.1.0: 衣装モードでは Load のラベルを「追加」に変更（キャラモードは上記の Add/Replace に置き換え）
                string loadLabel = SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.Scene
                    ? "Load"
                    : "\u8ffd\u52a0"; // 追加
                GUI.enabled = _selectedIndex >= 0;
                if (GUILayout.Button(loadLabel, _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                {
                    LoadSelected();
                }
                GUI.enabled = true;

                // v3.1.0: Import/Delete はシーンモードのみ表示（キャラ/衣装モードでは対象外の操作）
                if (SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.Scene)
                {
                    GUI.enabled = _selectedIndex >= 0;
                    if (GUILayout.Button("Import", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                    {
                        ImportSelected();
                    }
                    GUI.enabled = true;

                    // デリート（二段階確認）
                    if (_deleteConfirm)
                    {
                        if (Time.time - _deleteConfirmTime > DeleteResetSeconds)
                        {
                            _deleteConfirm = false;
                        }
                        GUI.backgroundColor = new Color(0.9f, 0.2f, 0.2f);
                        GUI.enabled = _selectedIndex >= 0;
                        if (GUILayout.Button("Delete?", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                        {
                            DeleteSelected();
                            _deleteConfirm = false;
                        }
                        GUI.enabled = true;
                        GUI.backgroundColor = Color.white;
                    }
                    else
                    {
                        GUI.enabled = _selectedIndex >= 0;
                        if (GUILayout.Button("Delete", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
                        {
                            _deleteConfirm = true;
                            _deleteConfirmTime = Time.time;
                        }
                        GUI.enabled = true;
                    }
                }
            }

            if (GUILayout.Button("Close", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
            {
                CloseScene();
            }

            // 右端のリサイズエッジ/スクロールバーとの重なりを避ける（ボタン群の右側に余白）
            GUILayout.Space(BottomBarRightPadding);

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // 全件数ラベル幅
        private float CountLabelWidth { get { return Mathf.Max(60f, FontSizeVal * 4.5f); } }

        // ═══════════════════════════════════════════════════════
        // ツリーペイン（SceneTree.cs の DrawNode を統合）
        // ═══════════════════════════════════════════════════════

        private void DrawTreePanel(Rect panelRect)
        {
            GUILayout.BeginArea(panelRect);

            // 検索 + 更新
            GUILayout.BeginHorizontal();
            GUILayout.Label("\u26b2", GUILayout.Width(16)); // ⚲
            string newFilter = GUILayout.TextField(_treeFilter, _filterStyle, GUILayout.ExpandWidth(true));
            if (newFilter != _treeFilter)
            {
                _treeFilter = newFilter;
                _dirChildrenCache.Clear();
            }
            if (GUILayout.Button("\u21bb", _toolbarButtonStyle, GUILayout.Width(24))) // ↻
            {
                _expandedFolders.Clear();
                _dirChildrenCache.Clear();
                RescanFiles();
            }
            GUILayout.EndHorizontal();

            // ツリースクロール
            ApplyScrollbarSkin();
            _treeScroll = GUILayout.BeginScrollView(_treeScroll);

            // ルート群: ローカルルート + 設定されたネットワークフォルダを同じ深さ(0)で並べて描画
            List<string> roots = new List<string>();
            bool localAdded = false;
            // v3.2.0: キャラ/衣装モード中はモードルート群をツリールートとして表示する
            // （シーンルート（studio/scene + 設定フォルダ群）の代わりに、モードルート配下だけを参照させる）
            if (SceneExplorerPlugin.CurrentBrowserMode != SceneExplorerPlugin.BrowserMode.Scene)
            {
                string[] modeRoots = SceneExplorerPlugin.GetModeRootFolders();
                for (int i = 0; i < modeRoots.Length; i++)
                {
                    string root = modeRoots[i];
                    if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    {
                        roots.Add(root);
                    }
                }
                // v3.3.1: 先頭ルートがローカル（UserData 配下）なら「ローカル」ラベルを付与
                // （シーンモードと同じ「ローカル + 設定フォルダ群」の並びに統一したため）
                string modeLocal = SceneExplorerPlugin.GetModeLocalRoot();
                if (roots.Count > 0 && modeLocal != null &&
                    string.Equals(roots[0], modeLocal, StringComparison.OrdinalIgnoreCase))
                {
                    localAdded = true;
                }
            }
            else
            {
                string localRoot = UserData.Create("studio/scene");
                if (!string.IsNullOrEmpty(localRoot) && Directory.Exists(localRoot))
                {
                    roots.Add(localRoot);
                    localAdded = true;
                }
                string[] configuredRoots = SceneExplorerPlugin.ScenePaths.GetConfiguredSceneFolders();
                if (configuredRoots != null)
                {
                    for (int i = 0; i < configuredRoots.Length; i++)
                    {
                        string root = configuredRoots[i];
                        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                        {
                            roots.Add(root);
                        }
                    }
                }
            }
            if (roots.Count == 0)
            {
                GUILayout.Label("\u30d5\u30a9\u30eb\u30c0\u304c\u898b\u3064\u304b\u308a\u307e\u305b\u3093"); // フォルダが見つかりません
            }
            else
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    string forcedName = (localAdded && i == 0) ? "\u30ed\u30fc\u30ab\u30eb" : null; // ローカル
                    DrawNode(roots[i], 0, i == roots.Count - 1, forcedName);
                }
            }

            GUILayout.EndScrollView();
            RestoreScrollbarSkin();
            GUILayout.EndArea();
        }

        private void DrawNode(string folderPath, int indent, bool isLast, string forcedName = null)
        {
            string name = forcedName;
            if (string.IsNullOrEmpty(name))
            {
                name = Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(name)) name = folderPath;
            }

            if (!PassesFilter(folderPath, name)) return;

            var e = Event.current;
            bool hasChildren = HasSubdirectories(folderPath);
            bool isExpanded = _expandedFolders.Contains(folderPath);
            string currentFolder = GetCurrentBrowserFolder();
            bool isSelected = string.Equals(folderPath, currentFolder, StringComparison.OrdinalIgnoreCase);

            // ── ノード行 ──
            var nodeContent = new GUIContent((isSelected ? "\u25b6 " : "") + name); // ▶ 選択中
            float nodeHeight = Mathf.Max(_nodeButtonStyle.CalcHeight(nodeContent, 400f), 18f);
            Rect lineRect = GUILayoutUtility.GetRect(new GUIContent(), GUIStyle.none, GUILayout.Height(nodeHeight));

            // 枝記号
            float treeX = lineRect.x + indent * IndentPerLevel + ToggleWidth;
            if (indent > 0)
            {
                GUI.color = new Color(0.38f, 0.42f, 0.52f);
                DrawBranchLines(lineRect, indent, treeX, isLast);
                GUI.color = Color.white;
            }

            // トグル
            if (hasChildren)
            {
                Rect toggleRect = new Rect(treeX, lineRect.y, ToggleWidth, lineRect.height);
                string toggleLabel = isExpanded ? "\u25bc" : "\u25b6"; // ▼ ▶
                if (GUI.Button(toggleRect, toggleLabel, GUIStyle.none))
                {
                    ToggleExpand(folderPath);
                    GUI.changed = true;
                }
                treeX += ToggleWidth;
            }

            // ノードボタン
            Rect nodeRect = new Rect(treeX, lineRect.y, lineRect.xMax - treeX, lineRect.height);
            if (e.type == EventType.MouseDown && nodeRect.Contains(e.mousePosition) && e.clickCount == 1)
            {
                SelectFolder(folderPath);
                e.Use();
            }

            // ハイライト
            if (isSelected)
            {
                GUI.DrawTexture(lineRect, _selectedRowTex, ScaleMode.StretchToFill);
            }
            else if (nodeRect.Contains(e.mousePosition))
            {
                GUI.DrawTexture(lineRect, _hoverRowTex, ScaleMode.StretchToFill);
                _tooltipText = folderPath;
                Vector2 tipPos801 = GUIUtility.GUIToScreenPoint(e.mousePosition);
                _tooltipRect = new Rect(tipPos801.x + TooltipOffsetX, tipPos801.y + TooltipOffsetY, 340, 22);
            }

            GUI.Label(nodeRect, nodeContent, _nodeButtonStyle);

            // ── 子ノード（再帰描画）──
            if (isExpanded)
            {
                List<DirEntry> children = GetCachedChildren(folderPath);
                for (int i = 0; i < children.Count; i++)
                {
                    bool childIsLast = i == children.Count - 1;
                    DrawNode(children[i].FullPath, indent + 1, childIsLast);
                }
            }
        }

        private void DrawBranchLines(Rect lineRect, int indent, float treeX, bool isLast)
        {
            float midY = lineRect.y + lineRect.height * 0.5f;
            float branchX = treeX - TreeLineWidth;

            // 水平ブランチ
            DrawHorizontalLine(branchX + 2, treeX - 2, midY);

            // 垂直ライン
            if (isLast)
            {
                DrawVerticalLine(branchX, lineRect.y, midY);
            }
            else
            {
                DrawVerticalLine(branchX, lineRect.y, lineRect.yMax);
            }
        }

        private void DrawHorizontalLine(float x1, float x2, float y)
        {
            GUI.DrawTexture(new Rect(x1, y, x2 - x1, 1), _splitterTex);
        }

        private void DrawVerticalLine(float x, float y1, float y2)
        {
            GUI.DrawTexture(new Rect(x, y1, 1, y2 - y1), _splitterTex);
        }

        // ═══════════════════════════════════════════════════════
        // グリッドペイン（右側）— v2.2.0: スクロール式（全件表示）
        // ═══════════════════════════════════════════════════════

        // v3.2.3: サムネイルの縦横比（シーン=16:9 横長、キャラ/衣装=カード PNG 240x320 の 3:4 縦長）
        private float GetThumbAspect()
        {
            return (SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.Scene) ? 9f / 16f : 4f / 3f;
        }

        private void DrawGridPanel(Rect panelRect)
        {
            if (_items.Count == 0)
            {
                GUILayout.BeginArea(panelRect);
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("\u30b7\u30fc\u30f3\u30d5\u30a1\u30a4\u30eb\u304c\u3042\u308a\u307e\u305b\u3093", _pageLabelStyle); // シーンファイルがありません
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.EndArea();
                return;
            }

            // グリッド計算
            float cellW = _thumbSize + ItemPadX * 2;
            // v3.2.3: サムネ縦横比はモード別（シーン=16:9 固定、キャラ/衣装=カードの 3:4 に合わせ縦長）
            float thumbW = cellW - ItemPadX * 2f;           // サムネ幅 = セル幅 − 左右余白（= _thumbSize と一致）
            float thumbH = thumbW * GetThumbAspect();       // モード別アスペクト（シーン 16:9 / キャラ衣装 3:4）
            float cellH = thumbH + TextLineHeight * 3 + ItemPadY * 2 + 2f;  // テキスト直下の隙間は2px
            int cols = Mathf.Max(1, Mathf.FloorToInt((panelRect.width - 16) / (cellW + ItemSpacing)));
            float gridTotalW = cols * (cellW + ItemSpacing) - ItemSpacing;
            float offsetX = (panelRect.width - gridTotalW) / 2f;

            // 全件スクロール
            int totalItems = _items.Count;
            int rows = Mathf.CeilToInt((float)totalItems / cols);
            float contentH = rows * (cellH + ItemSpacing) + ItemPadY;
            Rect viewRect = new Rect(panelRect.x, panelRect.y, panelRect.width, panelRect.height);
            Rect contentRect = new Rect(0, 0, gridTotalW, contentH);

            ApplyScrollbarSkin();
            _gridScroll = GUI.BeginScrollView(viewRect, _gridScroll, contentRect);

            // 可視範囲カリング: スクロール位置から可視行のみ描画（全件ループ廃止）。
            // contentRect は全件分のまま（スクロールバー計算のため変更しない）。
            // Layout/Repaint 間で _gridScroll が変わらないため、上下1行バッファで一貫描画になる。
            float rowH = cellH + ItemSpacing;
            int firstRow = Mathf.Max(0, Mathf.FloorToInt(_gridScroll.y / rowH) - 1);
            int lastRow = Mathf.Min(rows - 1, Mathf.CeilToInt((_gridScroll.y + viewRect.height) / rowH) + 1);

            for (int row = firstRow; row <= lastRow; row++)
            {
                int itemIndex = row * cols;
                int maxCol = Mathf.Min(cols, totalItems - itemIndex);
                for (int col = 0; col < maxCol; col++, itemIndex++)
                {
                    float x = col * (cellW + ItemSpacing);
                    float y = row * (cellH + ItemSpacing) + ItemPadY;
                    var itemRect = new Rect(x, y, cellW, cellH);
                    DrawGridItem(itemRect, itemIndex);
                }
            }

            GUI.EndScrollView();
            RestoreScrollbarSkin();
        }

        private void DrawGridItem(Rect rect, int index)
        {
            var item = _items[index];
            var e = Event.current;
            bool isSelected = index == _selectedIndex;
            bool isHover = rect.Contains(e.mousePosition);

            // 背景
            if (isSelected)
            {
                GUI.DrawTexture(rect, _selectedItemTex, ScaleMode.StretchToFill);
            }
            else if (isHover)
            {
                GUI.DrawTexture(rect, _hoverItemTex, ScaleMode.StretchToFill);
            }

            // サムネイル（v3.2.3: モード別アスペクト。ScaleMode.ScaleToFit のため異比サムネも収まる）
            float thumbW = rect.width - ItemPadX * 2f;      // DrawGridPanel の cellW − ItemPadX*2 と同じ値
            float thumbH = thumbW * GetThumbAspect();       // シーン 16:9 / キャラ衣装 3:4
            float thumbX = rect.x + ItemPadX;               // 左右余白は ItemPadX に一致
            float thumbY = rect.y + ItemPadY;
            var thumbRect = new Rect(thumbX, thumbY, thumbW, thumbH);
            Texture2D tex = GetThumbnail(item);
            if (tex != null)
            {
                GUI.DrawTexture(thumbRect, tex, ScaleMode.ScaleToFit);
            }
            else
            {
                GUI.DrawTexture(thumbRect, _emptyThumbTex);
                GUI.Label(thumbRect, "\u2609", _pageLabelStyle); // ☉ プレースホルダー
            }

            // ファイル名（サムネ直下は2pxの隙間）
            // v3.1.0: 表示名を優先（キャラ/衣装モードでは名前表示。Scene モードでは FileName と同値）
            float textY = thumbRect.yMax + 2f;
            var nameRect = new Rect(rect.x + 2, textY, rect.width - 4, TextLineHeight);
            string displayLabel = string.IsNullOrEmpty(item.DisplayName) ? item.FileName : item.DisplayName;
            GUI.Label(nameRect, displayLabel, _selectedItemStyle);

            // 更新日時
            var dateRect = new Rect(rect.x + 2, nameRect.yMax, rect.width - 4, TextLineHeight);
            GUI.Label(dateRect, item.LastWriteTime.ToString("yyyy/MM/dd HH:mm"), _dateStyle);

            // ファイルサイズ
            var sizeRect = new Rect(rect.x + 2, dateRect.yMax, rect.width - 4, TextLineHeight);
            GUI.Label(sizeRect, FormatFileSize(item.FileSize), _dateStyle);

            // クリック処理
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                if (e.clickCount == 1)
                {
                    _selectedIndex = index;
                    e.Use();
                }
                else if (e.clickCount == 2)
                {
                    _selectedIndex = index;
                    LoadSelected();
                    e.Use();
                }
            }

            // ツールチップ
            if (isHover && e.type == EventType.Repaint)
            {
                _tooltipText = item.FileName + "\n" + item.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss") + "\n" + FormatFileSize(item.FileSize);
                Vector2 tipPos963 = GUIUtility.GUIToScreenPoint(e.mousePosition);
                _tooltipRect = new Rect(tipPos963.x + TooltipOffsetX, tipPos963.y + TooltipOffsetY, 300, 46);
            }
        }

        // ═══════════════════════════════════════════════════════
        // ツールチップ描画（v2.2.0: マウス追従＋画面端クランプ）
        // ═══════════════════════════════════════════════════════

        private void DrawTooltip()
        {
            if (string.IsNullOrEmpty(_tooltipText)) return;

            // ツールチップサイズを計算
            Vector2 size = _tooltipStyle.CalcSize(new GUIContent(_tooltipText));
            float tw = size.x + _tooltipStyle.padding.left + _tooltipStyle.padding.right;
            float th = size.y + _tooltipStyle.padding.top + _tooltipStyle.padding.bottom;

            // マウス位置からオフセット
            float tx = _tooltipRect.x;
            float ty = _tooltipRect.y;

            // 画面端クランプ（右端・下端にはみ出さないように）
            float screenW = Screen.width;
            float screenH = Screen.height;
            if (tx + tw > screenW - TooltipPad)
            {
                tx = screenW - tw - TooltipPad;
            }
            if (ty + th > screenH - TooltipPad)
            {
                ty = screenH - th - TooltipPad;
            }
            if (tx < TooltipPad) tx = TooltipPad;
            if (ty < TooltipPad) ty = TooltipPad;

            Rect finalRect = new Rect(tx, ty, tw, th);
            GUI.Label(finalRect, _tooltipText, _tooltipStyle);
        }

        // ═══════════════════════════════════════════════════════
        // スクロールバー用カスタムスキン管理（v3.0.3）
        // GUI.skin を永続的に汚さないよう、一時差替＋即復元する。
        // ═══════════════════════════════════════════════════════

        private GUIStyle _scrollTrackStyle;
        private GUIStyle _scrollThumbStyle;
        private GUIStyle _origScrollbar;
        private GUIStyle _origScrollbarThumb;
        private GUIStyle _origHScrollbar;
        private GUIStyle _origHScrollbarThumb;

        private void ApplyScrollbarSkin()
        {
            if (_scrollTrackStyle == null)
            {
                _scrollTrackStyle = new GUIStyle(GUI.skin.verticalScrollbar);
                _scrollTrackStyle.normal.background = _scrollbarTrackTex;
                _scrollTrackStyle.fixedWidth = 14f;
            }
            if (_scrollThumbStyle == null)
            {
                _scrollThumbStyle = new GUIStyle(GUI.skin.verticalScrollbarThumb);
                _scrollThumbStyle.normal.background = _scrollbarThumbTex;
                _scrollThumbStyle.fixedWidth = 14f;
                _scrollThumbStyle.contentOffset = Vector2.zero;
            }
            // 既存スキンのスクロールバーだけ一時的に差替（他のスタイルは汚さない）
            _origScrollbar = GUI.skin.verticalScrollbar;
            _origScrollbarThumb = GUI.skin.verticalScrollbarThumb;
            _origHScrollbar = GUI.skin.horizontalScrollbar;
            _origHScrollbarThumb = GUI.skin.horizontalScrollbarThumb;
            GUI.skin.verticalScrollbar = _scrollTrackStyle;
            GUI.skin.verticalScrollbarThumb = _scrollThumbStyle;
            GUI.skin.horizontalScrollbar = _scrollTrackStyle;
            GUI.skin.horizontalScrollbarThumb = _scrollThumbStyle;
        }

        private void RestoreScrollbarSkin()
        {
            if (_origScrollbar != null)
            {
                GUI.skin.verticalScrollbar = _origScrollbar;
                GUI.skin.verticalScrollbarThumb = _origScrollbarThumb;
                GUI.skin.horizontalScrollbar = _origHScrollbar;
                GUI.skin.horizontalScrollbarThumb = _origHScrollbarThumb;
                _origScrollbar = null;
            }
        }

        // ═══════════════════════════════════════════════════════
        // アクション
        // ═══════════════════════════════════════════════════════

        private void LoadSelected()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _items.Count) return;
            var item = _items[_selectedIndex];
            try
            {
                // v3.1.0: モード別ロード（キャラ/衣装モードはブラウザを閉じず連続追加可能）
                switch (SceneExplorerPlugin.CurrentBrowserMode)
                {
                    case SceneExplorerPlugin.BrowserMode.CharaFemale:
                        AddOrReplaceChara(item.FilePath, 1);   // 1 = 女
                        return;
                    case SceneExplorerPlugin.BrowserMode.CharaMale:
                        AddOrReplaceChara(item.FilePath, 0);   // 0 = 男
                        return;
                    case SceneExplorerPlugin.BrowserMode.Coordinate:
                        ApplyCoordinate(item.FilePath);
                        return;
                }

                SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] Loading: " + item.FilePath);
                StartCoroutine(LoadSceneRoutine(item.FilePath));
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogError("[SceneBrowser] 追加に失敗しました: " + item.FilePath + ": " + ex.Message);
            }
        }

        private IEnumerator LoadSceneRoutine(string path)
        {
            _loading = true;
            _visible = false;
            yield return Studio.Studio.Instance.LoadSceneCoroutine(path);
            yield return null;
            try
            {
                Singleton<Manager.Scene>.Instance.UnLoad();
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] シーン読み込み後のダイアログ破棄に失敗: " + ex.Message);
            }
            _loading = false;
        }

        // v3.2.0: 選択中の同性別 OCIChar がいれば置き換え、いなければ追加
        // 標準 CharaList.ChangeCharaFemale/Male と同一方式（obj\CharaList_decompiled.cs:123-135）。
        // GuideObjectManager が未初期化等の場合は従来の追加にフォールバックする。
        private void AddOrReplaceChara(string path, int sex)
        {
            // v3.4.0: メインゲーム（Koikatu）ではスタジオ API（OCIChar / Studio.Instance）が使えないため、
            // キャラエディタの標準ロード経路（LoadFileLimited + Reload）で直接読み込む
            if (SceneExplorerPlugin.IsMainGame)
            {
                // v3.4.0: メインゲームのキャラ保存モードではダブルクリック = 選択カードへ上書き保存
                if (SceneExplorerPlugin.IsMainGameCharaSaveMode)
                {
                    SaveCharaInMainGame(path, true);
                    return;
                }
                LoadCharaInMainGame(path, sex);
                return;
            }
            Studio.OCIChar[] targets = CollectSameSexChara(sex);

            if (targets != null && targets.Length > 0)
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    try { targets[i].ChangeChara(path); }
                    catch (Exception ex) { SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] キャラ置き換え失敗: " + path + " - " + ex.Message); }
                }
                SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] キャラ置き換え: " + path + " x" + targets.Length);
            }
            else if (sex == 1)
            {
                try { Studio.Studio.Instance.AddFemale(path); }
                catch (Exception ex) { SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] キャラ追加失敗: " + path + " - " + ex.Message); }
            }
            else
            {
                try { Studio.Studio.Instance.AddMale(path); }
                catch (Exception ex) { SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] キャラ追加失敗: " + path + " - " + ex.Message); }
            }
        }

        // v3.4.0: メインゲームのキャラロード（キャラエディタの標準経路を直接実行）
        // sex はスタジオ慣例（1=女 / 0=男）で受け取り、LoadFileLimited の Byte sex（0=女 / 1=男）へ変換する
        private void LoadCharaInMainGame(string path, int sex)
        {
            try
            {
                ChaCustom.CustomBase customBase = FindObjectOfType<ChaCustom.CustomBase>();
                if (customBase == null)
                {
                    SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] メインゲームキャラロード失敗: CustomBase が見つかりません: " + path);
                    return;
                }
                ChaControl chaCtrl = customBase.chaCtrl;
                if (chaCtrl == null)
                {
                    SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] メインゲームキャラロード失敗: chaCtrl が null です: " + path);
                    return;
                }
                // 顔・体型・髪・パラメータ・衣装をすべてカードから読み込む
                bool ok = chaCtrl.chaFile.LoadFileLimited(path, (byte)(1 - sex), true, true, true, true, true);
                if (!ok)
                {
                    SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] メインゲームキャラロード失敗（LoadFileLimited が false）: " + path);
                    return;
                }
                chaCtrl.Reload(false, false, false, false);
                SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] メインゲームキャラロード: " + path);
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] メインゲームキャラロード失敗: " + path + " - " + ex.Message);
            }
        }

        // v3.4.0: メインゲームのキャラ保存（キャラエディタの標準経路を直接実行）
        // overwrite=true なら選択カードへ上書き、false なら指定パスに新規保存。
        // ゲーム本体と同一の引数（sex=(byte)-1 / newFile=false）で SaveCharaFile を呼ぶ
        // （CustomControl.<Start>m__8 の IL デコードで確認: 新規/上書きとも同一引数）。
        private void SaveCharaInMainGame(string path, bool overwrite)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                ChaCustom.CustomBase customBase = FindObjectOfType<ChaCustom.CustomBase>();
                if (customBase == null)
                {
                    SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] メインゲームキャラ保存失敗: CustomBase が見つかりません: " + path);
                    return;
                }
                ChaControl chaCtrl = customBase.chaCtrl;
                if (chaCtrl == null)
                {
                    SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] メインゲームキャラ保存失敗: chaCtrl が null です: " + path);
                    return;
                }
                bool ok = chaCtrl.chaFile.SaveCharaFile(path, unchecked((byte)-1), false);
                if (!ok)
                {
                    SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] メインゲームキャラ保存失敗（SaveCharaFile が false）: " + path);
                    return;
                }
                SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] メインゲームキャラ保存: " + path + (overwrite ? " (overwrite)" : ""));
                // 保存したカードを一覧に反映
                RescanFiles();
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] メインゲームキャラ保存失敗: " + path + " - " + ex.Message);
            }
        }

        // v3.4.0: 新規保存用のフルパスを組み立てる（現在のブラウザフォルダ + ファイル名、.png 拡張子を補完）
        private string BuildMainGameSavePath(string fileName)
        {
            fileName = fileName.Trim();
            if (fileName.Length == 0) return null;
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                fileName += ".png";
            string folder = SceneExplorerPlugin.CurrentBrowserFolder;
            if (string.IsNullOrEmpty(folder))
            {
                string[] roots = SceneExplorerPlugin.GetModeRootFolders();
                folder = (roots.Length > 0) ? roots[0] : "";
            }
            if (string.IsNullOrEmpty(folder)) return null;
            return Path.Combine(folder, fileName);
        }

        // v3.2.1: 選択中の同性別 OCIChar を収集する（標準 CharaList と同一方式。失敗時は null）
        private Studio.OCIChar[] CollectSameSexChara(int sex)
        {
            try
            {
                var gom = Singleton<Studio.GuideObjectManager>.Instance;
                if (gom == null || gom.selectObjectKey == null) return null;
                return (from v in gom.selectObjectKey
                        select Studio.Studio.GetCtrlInfo(v) as Studio.OCIChar into v
                        where v != null
                        where v.oiCharInfo.sex == sex
                        select v).ToArray();
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] 選択キャラ収集に失敗: " + ex.Message);
                return null;
            }
        }

        // v3.2.1: 常に追加（ボトムバーの Add ボタン用）
        private void AddSelected(string path, int sex)
        {
            try
            {
                if (sex == 1)
                    Studio.Studio.Instance.AddFemale(path);
                else
                    Studio.Studio.Instance.AddMale(path);
                SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] キャラ追加: " + path);
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] キャラ追加失敗: " + path + " - " + ex.Message);
            }
        }

        // v3.2.1: 常に置き換え（ボトムバーの Replace ボタン用。同性別キャラ未選択なら警告のみ）
        private void ReplaceSelected(string path, int sex)
        {
            Studio.OCIChar[] targets = CollectSameSexChara(sex);
            if (targets == null || targets.Length == 0)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] Replace: シーン内で同性別キャラクターを選択してください");
                return;
            }
            for (int i = 0; i < targets.Length; i++)
            {
                try { targets[i].ChangeChara(path); }
                catch (Exception ex) { SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] キャラ置き換え失敗: " + path + " - " + ex.Message); }
            }
            SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] キャラ置き換え: " + path + " x" + targets.Length);
        }

        // v3.2.3: 服を変えずに読み込み（顔・体型・髪のみ差し替え。Keep Clothes ボタン用）
        private void KeepClothesSelected(string path, int sex)
        {
            Studio.OCIChar[] targets = CollectSameSexChara(sex);
            if (targets == null || targets.Length == 0)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] KeepClothes: シーン内で同性別キャラクターを選択してください");
                return;
            }
            for (int i = 0; i < targets.Length; i++)
            {
                try { ApplyKeepClothes(targets[i], path); }
                catch (Exception ex) { SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] 服維持読込失敗: " + path + " - " + ex.Message); }
            }
            SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] 服を変えずに読込: " + path + " x" + targets.Length);
        }

        // v3.2.3: 標準 ChangeChara の骨組みで、Reload だけ服維持（noChangeClothes:true）にする
        private void ApplyKeepClothes(Studio.OCIChar ociChar, string path)
        {
            // 髪ボーンを除去（ChangeChara と同一手順）
            foreach (var bone in ociChar.listBones.Where(v => v.boneGroup == Studio.OIBoneInfo.BoneGroup.Hair).ToList())
            {
                Singleton<Studio.GuideObjectManager>.Instance.Delete(bone.guideObject);
            }
            ociChar.listBones = ociChar.listBones.Where(v => v.boneGroup != Studio.OIBoneInfo.BoneGroup.Hair).ToList();
            var hairKeys = ociChar.oiCharInfo.bones.Where(b => b.Value.group == Studio.OIBoneInfo.BoneGroup.Hair).Select(b => b.Key).ToArray();
            for (int i = 0; i < hairKeys.Length; i++) ociChar.oiCharInfo.bones.Remove(hairKeys[i]);
            ociChar.hairDynamic = null;
            ociChar.skirtDynamic = null;

            var charInfo = ociChar.charInfo;
            charInfo.chaFile.LoadCharaFile(path, byte.MaxValue, noLoadPng: true);
            charInfo.ChangeCoordinateType((ChaFileDefine.CoordinateType)charInfo.fileStatus.coordinateType);
            // 服だけ維持して顔・髪・体型を新カードで再構築（ChangeChara の Reload() フル版と対）
            charInfo.Reload(noChangeClothes: true, noChangeHead: false, noChangeHair: false, noChangeBody: false);
            ociChar.treeNodeObject.textName = charInfo.chaFile.parameter.fullname;

            try { Studio.AddObjectAssist.InitHairBone(ociChar, Singleton<Studio.Info>.Instance.dicBoneInfo); }
            catch { /* 髪ボーン初期化失敗は無視（ChangeChara も同様の耐障害性） */ }
            try { ociChar.hairDynamic = Studio.AddObjectFemale.GetHairDynamic(charInfo.objHair); }
            catch { }
            try { ociChar.skirtDynamic = Studio.AddObjectFemale.GetSkirtDynamic(charInfo.objClothes); }
            catch { }
        }

        // v3.1.0: 選択中キャラに衣装を適用（未選択なら何もしない）
        private void ApplyCoordinate(string path)
        {
            var targets = Studio.Studio.GetSelectObjectCtrl();
            if (targets == null)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] スタジオインスタンス未初期化のため衣装適用を中止: " + path);
                return;
            }
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

        private void ImportSelected()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _items.Count) return;
            var item = _items[_selectedIndex];
            try
            {
                SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] Importing: " + item.FilePath);
                Studio.Studio.Instance.ImportScene(item.FilePath);
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogError("[SceneBrowser] Import failed: " + ex.Message);
            }
        }

        private void DeleteSelected()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _items.Count) return;
            var item = _items[_selectedIndex];
            try
            {
                SceneExplorerPlugin.Log.LogInfo("[SceneBrowser] Deleting: " + item.FilePath);
                File.Delete(item.FilePath);
                RemoveThumbnailFromCache(item.FilePath);
                _items.RemoveAt(_selectedIndex);
                _selectedIndex = -1;
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogError("[SceneBrowser] Delete failed: " + ex.Message);
            }
        }

        private void CloseScene()
        {
            // v3.1.0: キャラ/衣装モード中はダイアログシーンが無いため UnLoad せず、モード解除と標準パネルの復元のみ行う
            if (SceneExplorerPlugin.CurrentBrowserMode != SceneExplorerPlugin.BrowserMode.Scene)
            {
                // v3.4.0: メインゲームでは標準の閉じる処理（btnClose.onClick.Invoke → ウィンドウ全体非表示）を実行する。
                // ポーリング側（DetectMainGameCharaLoad / DetectMainGameCharaSave）の終了検知で標準パネル復元・モード解除が完了する
                if (SceneExplorerPlugin.IsMainGame)
                {
                    if (SceneExplorerPlugin.IsMainGameCharaSaveMode)
                    {
                        SceneExplorerPlugin.CloseMainGameCharaSave();
                    }
                    else
                    {
                        SceneExplorerPlugin.CloseMainGameCharaLoad();
                    }
                }
                SceneExplorerPlugin.RequestSceneMode("Close");
                _visible = false;
                _selectedIndex = -1;
                _tooltipText = "";
                RestoreStandardPanels();
                return;
            }
            try
            {
                Singleton<Manager.Scene>.Instance.UnLoad();
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogError("[SceneBrowser] Close failed: " + ex.Message);
            }
        }

        // v3.1.0: キャラ/衣装モード開始時に標準パネルを非表示にする（遷移検出時のみ呼ぶ）
        // 実際に非アクティブ化したパネルのみ _hiddenModePanels に記録し、モード解除時の復元対象とする。
        // モードが切り替わった場合（女→男等）は前モードのスナップショットを破棄して新たに記録する
        // （前モードのパネルはタブ切替により元々非アクティブ化されるため復元対象から外す）。
        private void HideModePanels()
        {
            _hiddenModePanels.Clear();
            foreach (var cl in SceneExplorerPlugin.activeCharaLists)
            {
                if (cl != null && cl.gameObject.activeInHierarchy)
                {
                    cl.gameObject.SetActive(false);
                    _hiddenModePanels.Add(cl.gameObject);
                }
            }
            if (_costumeRoot == null) _costumeRoot = ResolveCostumeRoot();
            if (_costumeRoot != null && _costumeRoot.activeInHierarchy)
            {
                _costumeRoot.SetActive(false);
                _hiddenModePanels.Add(_costumeRoot);
            }
        }

        // v3.1.0: モード解除時に標準パネルを復元する（遷移検出時のみ呼ぶ）
        // スナップショット（このモードで実際に隠したパネル）のみ復元し、元々非アクティブだった
        // パネルには触れない（タブ選択状態を尊重）。モード開始時にアクティブだったパネルが
        // タブ切替で非アクティブ化された場合も復元しない。
        private void RestoreStandardPanels()
        {
            // 破棄済み CharaList 参照を除去（スタジオシーン再ロード対策。Unity の == null オーバーロードで判定）
            SceneExplorerPlugin.activeCharaLists.RemoveAll(l => l == null);
            foreach (var go in _hiddenModePanels)
            {
                if (go != null && !go.activeInHierarchy) go.SetActive(true);
            }
            _hiddenModePanels.Clear();
            // v3.1.0: Close 後 select==4 残留による誤再発火防止。
            // 別キャラ選択時（ociChar setter → UpdateInfo → OnClickRoot(4)）に衣装モードが勝手に開くのを防ぐため、
            // モード解除時に MPCharCtrl.select を -1 にリセットする（タブ表示は次の OnClickRoot で同期される）。
            if (_mpCharCtrl != null)
            {
                var selectField = HarmonyLib.AccessTools.Field(typeof(Studio.MPCharCtrl), "select");
                if (selectField != null) selectField.SetValue(_mpCharCtrl, -1);
            }
            _mpCharCtrl = null;   // 次回モード開始時に再解決させる
            _costumeRoot = null;
        }

        // v3.1.0: costumeInfo フィールド（private）のルート GameObject を解決する（リフレクション結果はキャッシュ）
        private UnityEngine.GameObject ResolveCostumeRoot()
        {
            if (_mpCharCtrl == null)
                _mpCharCtrl = UnityEngine.Object.FindObjectOfType<Studio.MPCharCtrl>();
            if (_mpCharCtrl == null) return null;
            try
            {
                var fi = HarmonyLib.AccessTools.Field(typeof(Studio.MPCharCtrl), "costumeInfo");
                if (fi == null) return null;
                var ci = fi.GetValue(_mpCharCtrl);
                if (ci == null) return null;
                var rootFi = HarmonyLib.AccessTools.Field(ci.GetType(), "objRoot") ?? HarmonyLib.AccessTools.Field(ci.GetType(), "root");
                if (rootFi == null) return null;
                return rootFi.GetValue(ci) as UnityEngine.GameObject;
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] costumeInfo ルート解決失敗: " + ex.Message);
                return null;
            }
        }

		// ═══════════════════════════════════════════════════════
		// データ取得・ソート
		// ═══════════════════════════════════════════════════════

        private void CheckFolderChanged()
        {
            // v3.2.2: モード切替や再表示で CurrentBrowserFolder が null のままの場合に保存済みフォルダを復元
            // （復元されると current が変わるため下の不一致判定で RescanFiles が走る）
            TryRestoreLastFolder();
            string current = GetCurrentBrowserFolder();
            if (!string.Equals(current, _lastScannedFolder, StringComparison.OrdinalIgnoreCase))
            {
                RescanFiles();
            }
        }

        private void RescanFiles()
        {
            _lastScannedFolder = GetCurrentBrowserFolder();
            _items.Clear();
            _selectedIndex = -1;
            _gridScroll = Vector2.zero;

            // 非同期サムネイル要求を破棄（フォルダ切替後の古い要求が溜まらないように）。
            // 実行中スレッドの結果は ProcessThumbnailResults の参照一致検証で無害化される。
            lock (_thumbReqLock) _thumbReqQueue.Clear();
            lock (_thumbResLock) _thumbResQueue.Clear();

            string basePath = _lastScannedFolder;
            if (string.IsNullOrEmpty(basePath))
            {
                // v3.2.0: モード中に CurrentBrowserFolder が null（モードルートが空 = 設定ミス）なら、
                // シーンルート（GetBrowserBasePath）をフォールバック走査してキャラカードを無駄パースしないよう空一覧で終了。
                // 判定はモード中かどうか（ルートが空でもモード中ならフォールバックしない）
                if (SceneExplorerPlugin.CurrentBrowserMode != SceneExplorerPlugin.BrowserMode.Scene)
                {
                    SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] モードルートが空のため一覧を空にします（フォルダ設定を確認してください）");
                    return;
                }
                basePath = SceneExplorerPlugin.GetBrowserBasePath();
            }
            // v3.2.0: モードルートが存在する場合、相対パス（例: "chara/female"）をフルパスへ解決
            // （GetModeRootFolders は Directory.Exists 済みフルパスを返すため、通常は IsPathRooted が true でスキップされる）
            if (SceneExplorerPlugin.GetModeRootFolders().Length > 0 && !Path.IsPathRooted(basePath))
            {
                basePath = Path.Combine(UserData.Path, basePath);
            }
            if (string.IsNullOrEmpty(basePath) || !Directory.Exists(basePath)) return;

            try
            {
                string[] files = SceneExplorerPlugin.ScenePaths.ScanFolder(basePath, "*.png");
                if (files == null) return;

                for (int i = 0; i < files.Length; i++)
                {
                    string path = files[i];
                    try
                    {
                        // v3.2.2: 表示名はファイル名（LoadCharaFile の同期フルパースを一覧で行わない。
                        // 数百ファイルで「必要mod探索エラー」と遅延が出るため。破損カードはロード時に無視される）
                        var fi = new FileInfo(path);
                        var item = new SceneItem
                        {
                            FilePath = path,
                            FileName = fi.Name,
                            DisplayName = System.IO.Path.GetFileNameWithoutExtension(path),
                            LastWriteTime = fi.LastWriteTime,
                            FileSize = fi.Length,
                            Thumbnail = null,
                            ThumbLoaded = false
                        };
                        _items.Add(item);
                    }
                    catch (Exception ex)
                    {
                        SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] FileInfo error: " + path + " - " + ex.Message);
                    }
                }

                SortItems();
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogError("[SceneBrowser] Scan failed: " + ex.Message);
            }
        }

        private void SortItems()
        {
            switch (_sortMode)
            {
                case SortMode.Name:
                    _items.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
                    break;
                case SortMode.Date:
                    _items.Sort((a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime));
                    break;
                case SortMode.Size:
                    _items.Sort((a, b) => a.FileSize.CompareTo(b.FileSize));
                    break;
            }
            if (_sortDescending) _items.Reverse();
        }

        private void ToggleSort(SortMode mode)
        {
            if (_sortMode == mode)
            {
                _sortDescending = !_sortDescending;
            }
            else
            {
                _sortMode = mode;
                _sortDescending = true;
            }
            SortItems();
            // v3.2.1: ソート状態を永続化（次回起動時に復元）
            SceneExplorerPlugin.SortMode.Value = (int)_sortMode;
            SceneExplorerPlugin.SortDescending.Value = _sortDescending;
            SceneExplorerPlugin.ConfigFile.Save();
        }

        // ═══════════════════════════════════════════════════════
        // サムネイルキャッシュ
        // ═══════════════════════════════════════════════════════

        private Texture2D GetThumbnail(SceneItem item)
        {
            // v2.5.1: 破棄済みテクスチャ（Unityではnull扱い）なら再読み込みする
            if (item.ThumbLoaded && item.Thumbnail != null) return item.Thumbnail;

            Texture2D tex;
            if (_thumbCache.TryGetValue(item.FilePath, out tex))
            {
                item.Thumbnail = tex;
                item.ThumbLoaded = true;
                return tex;
            }

            // 非同期ロード: 要求キューに積み、このフレームではプレースホルダー（☉）を返す。
            // ThumbRequested フラグで二重要求を防止（メインスレッドからのみ呼ばれる）。
            if (!item.ThumbRequested)
            {
                item.ThumbRequested = true;
                EnqueueThumbnailRequest(item);
            }
            return item.Thumbnail;
        }

        // v2.5.0: KKCC圧縮シーンのサムネ読み込みを堅牢化。
        // ゲーム標準の PngAssist.LoadTexture は FileShare 指定なしで開くため、NAS等で他プロセス
        // （Syncthing等）が書き込み中だと IOException になり得る。FileShare.ReadWrite で開き、
        // PNGのIENDチャンクまでのPNG部分のみ読み取って Texture2D 化する（KKCCの付加データは無視）。
        // 非同期化: ファイルI/O部分を分離。Unity API に一切触れないためバックグラウンドスレッドで実行可。
        private static byte[] ReadThumbnailBytes(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] signature = new byte[8];
                    int sigRead = fs.Read(signature, 0, 8);
                    if (sigRead < 8) return null;
                    // PNGシグネチャ: 89 50 4E 47 0D 0A 1A 0A
                    if (signature[0] != 0x89 || signature[1] != 0x50 || signature[2] != 0x4E || signature[3] != 0x47
                        || signature[4] != 0x0D || signature[5] != 0x0A || signature[6] != 0x1A || signature[7] != 0x0A)
                    {
                        return null;
                    }

                    long fileSize = fs.Length;
                    int pos = 8;
                    bool foundIend = false;
                    for (int chunk = 0; chunk < 300; chunk++)
                    {
                        if ((long)pos + 8 > fileSize) break;
                        byte[] header = new byte[8];
                        fs.Position = pos;
                        int headerRead = fs.Read(header, 0, 8);
                        if (headerRead < 8) break;

                        int len = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                        byte t0 = header[4];
                        byte t1 = header[5];
                        byte t2 = header[6];
                        byte t3 = header[7];
                        if (len < 0 || (long)pos + 12 + len > fileSize) break;
                        if (t0 == 0x49 && t1 == 0x45 && t2 == 0x4E && t3 == 0x44) // "IEND"
                        {
                            foundIend = true;
                            pos += 12 + len;
                            break;
                        }
                        pos += 12 + len;
                    }
                    if (!foundIend) return null;

                    // IENDチャンク終端までのPNG部分を読み取る
                    int pngSize = pos;
                    byte[] data = new byte[pngSize];
                    fs.Position = 0;
                    int totalRead = 0;
                    while (totalRead < pngSize)
                    {
                        int n = fs.Read(data, totalRead, pngSize - totalRead);
                        if (n <= 0) break;
                        totalRead += n;
                    }
                    if (totalRead < pngSize) return null;
                    return data;
                }
            }
            catch
            {
                return null;
            }
        }

        // ── 非同期サムネイルロード ──
        // 要求をキューに積み、バックグラウンド読み込みを開始する（メインスレッドからのみ呼ぶ）
        private void EnqueueThumbnailRequest(SceneItem item)
        {
            bool spawnWorker = false;
            lock (_thumbReqLock)
            {
                // キューが空の時だけワーカーを起動（無駄なスレッド生成を避ける）
                if (_thumbReqQueue.Count == 0) spawnWorker = true;
                _thumbReqQueue.Enqueue(item);
            }
            if (spawnWorker)
            {
                ThreadPool.QueueUserWorkItem(ThumbnailWorker);
            }
        }

        // バックグラウンドスレッド: 要求キューを一括取得し、PNGバイトを読み取って結果キューに積む。
        // Unity API（Texture2D 等）には一切触れない（触るとクラッシュするため禁止）。
        private void ThumbnailWorker(object state)
        {
            List<SceneItem> batch;
            lock (_thumbReqLock)
            {
                if (_thumbReqQueue.Count == 0) return;
                batch = new List<SceneItem>(_thumbReqQueue);
                _thumbReqQueue.Clear();
            }

            for (int i = 0; i < batch.Count; i++)
            {
                SceneItem item = batch[i];
                byte[] data = ReadThumbnailBytes(item.FilePath);
                lock (_thumbResLock)
                {
                    _thumbResQueue.Enqueue(new ThumbLoadResult { Item = item, Data = data });
                }
            }
        }

        // メインスレッド: 結果キューを1フレーム最大2件処理（Update の先頭で呼ぶ）
        private void ProcessThumbnailResults()
        {
            for (int n = 0; n < 2; n++)
            {
                ThumbLoadResult res;
                lock (_thumbResLock)
                {
                    if (_thumbResQueue.Count == 0) return;
                    res = _thumbResQueue.Dequeue();
                }
                // フォルダ切替後は SceneItem が破棄されている — 参照一致で検証し、破棄（デコードもしない）
                if (res.Item == null || !_items.Contains(res.Item)) continue;
                ApplyThumbnailResult(res.Item, res.Data);
            }
        }

        // メインスレッド: バイト列から Texture2D を生成（デコード・ガンマ補正・キャッシュ登録）
        private void ApplyThumbnailResult(SceneItem item, byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                // 読み込み失敗 — 再試行可能に戻す（従来の再ロード挙動と同等）
                item.ThumbRequested = false;
                return;
            }
            try
            {
                Texture2D result = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!result.LoadImage(data))
                {
                    Destroy(result);
                    item.ThumbRequested = false;
                    return;
                }
                // v3.0.15: 表示時に ^2.2 変換される環境のため、ピクセルを ^(1/2.2) に事前補正（UIテクスチャと同様の環境補正）
                Color[] px = result.GetPixels();
                for (int i = 0; i < px.Length; i++)
                {
                    px[i].r = Mathf.Pow(px[i].r, 0.4545f);
                    px[i].g = Mathf.Pow(px[i].g, 0.4545f);
                    px[i].b = Mathf.Pow(px[i].b, 0.4545f);
                }
                result.SetPixels(px);
                result.Apply();
                AddToThumbnailCache(item.FilePath, result);
                item.Thumbnail = result;
                item.ThumbLoaded = true;
                item.ThumbRequested = false;
            }
            catch
            {
                // サムネイル読み込み失敗は無視（再試行可能に戻す）
                item.ThumbRequested = false;
            }
        }

        private void AddToThumbnailCache(string path, Texture2D tex)
        {
            if (_thumbCache.ContainsKey(path))
            {
                _thumbCache[path] = tex;
                _thumbCacheOrder.Remove(path);
                _thumbCacheOrder.Add(path);
                return;
            }

            // 上限チェック — 古いものから破棄
            while (_thumbCacheOrder.Count >= MaxCacheSize)
            {
                string oldest = _thumbCacheOrder[0];
                _thumbCacheOrder.RemoveAt(0);
                Texture2D oldTex;
                if (_thumbCache.TryGetValue(oldest, out oldTex))
                {
                    _thumbCache.Remove(oldest);
                    if (oldTex != null) Destroy(oldTex);
                }
                // v2.5.1: 追い出されたアイテムは再読み込み可能に戻す。
                // 破棄済みテクスチャ参照（ThumbLoaded=true のまま）を残すと GetThumbnail が
                // 再読み込みせず null を返し続け、ファイル数が多いフォルダでサムネが表示されなくなる。
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].FilePath == oldest)
                    {
                        _items[i].ThumbLoaded = false;
                        _items[i].Thumbnail = null;
                        break;
                    }
                }
            }

            _thumbCache[path] = tex;
            _thumbCacheOrder.Add(path);
        }

        private void RemoveThumbnailFromCache(string path)
        {
            Texture2D tex;
            if (_thumbCache.TryGetValue(path, out tex))
            {
                _thumbCache.Remove(path);
                _thumbCacheOrder.Remove(path);
                if (tex != null) Destroy(tex);
            }
        }

        private void ClearThumbnailCache()
        {
            foreach (var tex in _thumbCache.Values)
            {
                if (tex != null) Destroy(tex);
            }
            _thumbCache.Clear();
            _thumbCacheOrder.Clear();
        }

        // ═══════════════════════════════════════════════════════
        // ツリー状態管理
        // ═══════════════════════════════════════════════════════

        private void ToggleExpand(string path)
        {
            if (_expandedFolders.Contains(path))
            {
                _expandedFolders.Remove(path);
            }
            else
            {
                _expandedFolders.Add(path);
            }
            _dirChildrenCache.Remove(path);
        }

        private void SelectFolder(string path)
        {
            // v3.2.0: キャラ/衣装モードではモードルート群より上へ移動させない（複数ルート対応）
            if (SceneExplorerPlugin.CurrentBrowserMode != SceneExplorerPlugin.BrowserMode.Scene)
            {
                string[] modeRoots = SceneExplorerPlugin.GetModeRootFolders();
                bool insideAnyRoot = false;
                for (int i = 0; i < modeRoots.Length; i++)
                {
                    // GetModeRootFolders は Directory.Exists 確認済みのフルパスを返す（ResolveFolderSetting 済み）
                    // プレフィックス誤判定（"C:\chara\female" と "C:\chara\female2"）を防ぐため等値 or セパレータ付き前方一致で判定
                    if (path.Equals(modeRoots[i], StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith(modeRoots[i] + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        insideAnyRoot = true;
                        break;
                    }
                }
                if (!insideAnyRoot && modeRoots.Length > 0)
                    path = modeRoots[0];   // いずれのルート配下でもなければ先頭ルートへ矯正
            }

            // プラグイン側のフィールドを更新（別タスクで実装）
            SceneExplorerPlugin.CurrentBrowserFolder = path;
            // v3.2.1: シーンモードでのみ最後に開いたフォルダを記憶（モードルートをシーンの記憶と混ぜない）
            // v3.3.1: バックスラッシュはエスケープせずそのまま保存（読込時に BepInEx がデコード、正規化で補正）
            if (SceneExplorerPlugin.CurrentBrowserMode == SceneExplorerPlugin.BrowserMode.Scene)
            {
                SceneExplorerPlugin.LastFolder.Value = path;
                SceneExplorerPlugin.ConfigFile.Save();
            }
            RescanFiles();
        }

        private bool HasSubdirectories(string path)
        {
            try
            {
                return Directory.GetDirectories(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private List<DirEntry> GetCachedChildren(string path)
        {
            List<DirEntry> children;
            if (_dirChildrenCache.TryGetValue(path, out children))
            {
                return children;
            }

            children = new List<DirEntry>();
            try
            {
                string[] dirs = Directory.GetDirectories(path);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string dir = dirs[i];
                    string dirName = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(dirName) && (string.IsNullOrEmpty(_treeFilter) || dirName.IndexOf(_treeFilter, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        children.Add(new DirEntry
                        {
                            Name = dirName,
                            FullPath = dir,
                            HasChildren = HasSubdirectories(dir)
                        });
                    }
                }
                children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[SceneBrowser] Dir scan error: " + path + " - " + ex.Message);
            }

            _dirChildrenCache[path] = children;
            return children;
        }

        private bool PassesFilter(string folderPath, string name)
        {
            if (string.IsNullOrEmpty(_treeFilter)) return true;
            if (name.IndexOf(_treeFilter, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // 子孫に一致するものがあるかチェック（再帰的表示用）
            try
            {
                string[] subDirs = Directory.GetDirectories(folderPath, "*" + _treeFilter + "*", SearchOption.AllDirectories);
                return subDirs.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════
        // ユーティリティ
        // ═══════════════════════════════════════════════════════

        private static string GetCurrentBrowserFolder()
        {
            return SceneExplorerPlugin.CurrentBrowserFolder;
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes.ToString() + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024).ToString() + " KB";
            return ((float)bytes / (1024 * 1024)).ToString("F1") + " MB";
        }

        private void CenterWindow()
        {
            float w = Mathf.Min(_lastSavedWidth, Screen.width - 40);
            float h = Mathf.Min(_lastSavedHeight, Screen.height - 40);
            _windowRect = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
        }

        private void ConstrainWindow()
        {
            // 画面より大きいウィンドウは画面に収める（DPI/解像度差の安全策）
            float screenW = Screen.width - 20f;
            float screenH = Screen.height - 20f;
            _windowRect.width = Mathf.Min(_windowRect.width, screenW);
            _windowRect.height = Mathf.Min(_windowRect.height, screenH);
            // 最小サイズも画面サイズ以下にクランプ（低解像度での画面はみ出し防止）
            _windowRect.width = Mathf.Max(_windowRect.width, Mathf.Min(MinWindowWidth, screenW));
            _windowRect.height = Mathf.Max(_windowRect.height, Mathf.Min(MinWindowHeight, screenH));
            // 安全なClamp（上限が負にならないように）
            float maxX = Mathf.Max(0f, Screen.width - _windowRect.width);
            float maxY = Mathf.Max(0f, Screen.height - _windowRect.height);
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, maxX);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, maxY);
        }
    }
}
