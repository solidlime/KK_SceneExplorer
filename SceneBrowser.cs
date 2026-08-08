using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Manager;
using UnityEngine;

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
        private const float ItemPadX = 6f;
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
        private bool _loading;
        private float _nextCheckTime;
        private bool _stylesReady;

        // ファイル一覧
        private List<SceneItem> _items = new List<SceneItem>();
        private int _selectedIndex = -1;
        private string _lastScannedFolder;

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
            public DateTime LastWriteTime;
            public long FileSize;
            public Texture2D Thumbnail;
            public bool ThumbLoaded;
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
        }

        private void Update()
        {
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

            // 他プラグイン（Skin Overlay Mod等）がGUI.matrixに残した変換を強制リセット。
            // これを行わないとスケール・オフセットが掛かり、ウィンドウが左上の小領域に縮小描画される。
            // SettingsUi.csは短小ウィンドウで影響が小さいため非顕在化しているが、原理は同じ。
            GUI.matrix = Matrix4x4.identity;

            // v2.0.6: 他プラグインがPopし忘れたGUIClipスタックを剥がす（クリップリーク対策）
            ResetClipLeak();

            // v3.0.1: モーダル化 — ウィンドウ外のみクリック吸収（ウィンドウ内操作を妨げない）。
            // 全画面1枚のButtonはUnity 5.6でウィンドウ内のMouseDownも消費して操作不能になるため、
            // ウィンドウRect（8px拡張）の外側を4分割した矩形にButtonを配置する。
            GUI.depth = 0;
            GUI.color = new Color(0, 0, 0, 0);
            GUI.backgroundColor = new Color(0, 0, 0, 0);
            {
                float margin = 8f;
                float wx = _windowRect.x - margin;
                float wy = _windowRect.y - margin;
                float ww = _windowRect.width + margin * 2f;
                float wh = _windowRect.height + margin * 2f;
                float sw = (float)Screen.width;
                float sh = (float)Screen.height;

                // 上: ウィンドウの上端より上
                float topH = Mathf.Max(0f, wy);
                if (topH > 0f) GUI.Button(new Rect(0f, 0f, sw, topH), _clearTex);

                // 下: ウィンドウの下端より下
                float bottomY = wy + wh;
                float bottomH = Mathf.Max(0f, sh - bottomY);
                if (bottomH > 0f) GUI.Button(new Rect(0f, bottomY, sw, bottomH), _clearTex);

                // 左: ウィンドウ左端より左（上・下を除く高さ）
                float sideY = wy;
                float sideH = wh;
                float leftW = Mathf.Max(0f, wx);
                if (leftW > 0f) GUI.Button(new Rect(0f, sideY, leftW, sideH), _clearTex);

                // 右: ウィンドウ右端より右（上・下を除く高さ）
                float rightX = wx + ww;
                float rightW = Mathf.Max(0f, sw - rightX);
                if (rightW > 0f) GUI.Button(new Rect(rightX, sideY, rightW, sideH), _clearTex);
            }
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;

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
        private bool ShouldBeVisible()
        {
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

            // タイトルバー描画
            if (Event.current.type == EventType.Repaint)
            {
                _titleBarStyle.Draw(titleRect, "\u2601 \u30b7\u30fc\u30f3\u3092\u958b\u304f", false, false, false, false); // ? シーンを開く
            }

            // v3.0.10: デバッグ — サムネ読み込み後の平均輝度を一時表示（読み込み後8秒間）
            if (LastThumbBrightness >= 0f && Time.realtimeSinceStartup < _brightnessShownUntil)
            {
                var dbgStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
                dbgStyle.normal.textColor = Color.yellow;
                var dbgRect = new Rect(0, titleRect.yMax + 2, fullRect.width, 18);
                GUI.Label(dbgRect, "Debug: サムネ読み込み後 平均輝度 = " + LastThumbBrightness.ToString("F3"), dbgStyle);
                // v3.0.12: 画面描画後の明るさを ReadPixels で実測（1回のみ）
                if (!_screenSampled)
                {
                    _screenSampled = true;
                    StartCoroutine(SampleThumbBrightness());
                }
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

            // v2.5.4: ボタン幅をFlexibleWidth化（ウィンドウ幅に応じて均等スケール）。ラベル英語化。
            GUI.enabled = _selectedIndex >= 0;
            if (GUILayout.Button("Load", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
            {
                LoadSelected();
            }
            GUI.enabled = true;

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

            if (GUILayout.Button("Close", _toolbarButtonStyle, GUILayout.MinWidth(FooterButtonWidth)))
            {
                CloseScene();
            }

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
            float cellH = _thumbSize + TextLineHeight * 3 + ItemPadY * 2 + ItemGap;
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

            // サムネイル
            float thumbX = rect.x + (rect.width - _thumbSize) / 2f;
            float thumbY = rect.y + ItemPadY;
            var thumbRect = new Rect(thumbX, thumbY, _thumbSize, _thumbSize);
            Texture2D tex = GetThumbnail(item);
            if (tex != null)
            {
                GUI.DrawTexture(thumbRect, tex, ScaleMode.ScaleToFit);
                // v3.0.12: 画面計測用にサムネ実描画領域を記録（パネル余白を除外、グローバル座標）
                float texAspect = tex.width > 0 && tex.height > 0 ? (float)tex.width / tex.height : 1f;
                float drawW = texAspect > 1f ? thumbRect.width : thumbRect.height * texAspect;
                float drawH = texAspect > 1f ? thumbRect.width / texAspect : thumbRect.height;
                _lastThumbDrawRect = new Rect(_windowRect.x + thumbRect.x + (thumbRect.width - drawW) / 2f,
                                              _windowRect.y + thumbRect.y + (thumbRect.height - drawH) / 2f,
                                              drawW, drawH);
            }
            else
            {
                GUI.DrawTexture(thumbRect, _emptyThumbTex);
                GUI.Label(thumbRect, "\u2609", _pageLabelStyle); // ☉ プレースホルダー
            }

            // ファイル名
            float textY = thumbRect.yMax + ItemGap;
            var nameRect = new Rect(rect.x + 2, textY, rect.width - 4, TextLineHeight);
            GUI.Label(nameRect, item.FileName, _selectedItemStyle);

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
            try
            {
                Singleton<Manager.Scene>.Instance.UnLoad();
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogError("[SceneBrowser] Close failed: " + ex.Message);
            }
        }

		// ═══════════════════════════════════════════════════════
		// データ取得・ソート
		// ═══════════════════════════════════════════════════════

        private void CheckFolderChanged()
        {
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

            string basePath = _lastScannedFolder;
            if (string.IsNullOrEmpty(basePath))
            {
                basePath = SceneExplorerPlugin.GetBrowserBasePath();
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
                        var fi = new FileInfo(path);
                        var item = new SceneItem
                        {
                            FilePath = path,
                            FileName = fi.Name,
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

            // ロード（非同期化が必要なら後で対応）
            try
            {
                tex = LoadSceneThumbnail(item.FilePath);
                if (tex != null)
                {
                    AddToThumbnailCache(item.FilePath, tex);
                    item.Thumbnail = tex;
                }
            }
            catch
            {
                // サムネイル読み込み失敗は無視
            }
            item.ThumbLoaded = true;
            return item.Thumbnail;
        }

        // v2.5.0: KKCC圧縮シーンのサムネ読み込みを堅牢化。
        // ゲーム標準の PngAssist.LoadTexture は FileShare 指定なしで開くため、NAS等で他プロセス
        // （Syncthing等）が書き込み中だと IOException になり得る。FileShare.ReadWrite で開き、
        // PNGのIENDチャンクまでのPNG部分のみ読み取って Texture2D 化する（KKCCの付加データは無視）。
        private static Texture2D LoadSceneThumbnail(string path)
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

                    // v3.0.9: PngAssist ではなく Unity 標準の LoadImage で直接読み込む（デコードが暗い問題の切り分け）
                    Texture2D result = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!result.LoadImage(data)) return null;
                    // v3.0.9: デバッグ — 読み込み後テクスチャの平均輝度をログ出力（原因切り分け用）
                    LogThumbnailBrightness(path, result);
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
                    return result;
                }
            }
            catch
            {
                return null;
            }
        }

        // v3.0.10: デバッグ用 — サムネ読み込み後の平均輝度（画面表示＋専用ログファイル。BepInEx ログ設定に依存しない）
        public static float LastThumbBrightness = -1f;
        private static float _brightnessShownUntil = 0f;
        // v3.0.13: 1ファイルの結果で断定しないため、最初の20ファイルの読み込み輝度を記録する
        private static int brightnessLogCount;
        // v3.0.12: デバッグ用 — 画面に描画されたサムネの実測（ReadPixels）。読み込み値との差で減衰箇所を特定
        private static bool _screenSampled;
        private static Rect _lastThumbDrawRect = new Rect(-1f, -1f, 0f, 0f);
        private static void LogThumbnailBrightness(string path, Texture2D tex)
        {
            if (brightnessLogCount >= 20) return;
            brightnessLogCount++;
            try
            {
                Color[] px = tex.GetPixels();
                float avg = 0f;
                for (int i = 0; i < px.Length; i++)
                {
                    avg += 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                }
                avg /= Mathf.Max(px.Length, 1);
                LastThumbBrightness = avg;
                _brightnessShownUntil = Time.realtimeSinceStartup + 8f;
                string msg = "[v3.0.13 Debug] サムネ読み込み後: " + Path.GetFileName(path) + " 平均輝度=" + avg.ToString("F3") + " (" + tex.width + "x" + tex.height + ")";
                try
                {
                    string root = Path.GetDirectoryName(Application.dataPath);
                    File.AppendAllText(Path.Combine(root, "KK_SceneExplorer_brightness.log"), msg + Environment.NewLine);
                }
                catch { }
                SceneExplorerPlugin.Log.LogWarning(msg);
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[v3.0.10 Debug] 輝度計測失敗: " + ex.Message);
            }
        }

        // v3.0.12: デバッグ — 画面に実際に描画されたサムネの平均輝度を ReadPixels で計測（1回のみ）
        private IEnumerator SampleThumbBrightness()
        {
            yield return new WaitForEndOfFrame();
            try
            {
                if (_lastThumbDrawRect.width <= 1f || _lastThumbDrawRect.height <= 1f) yield break;
                float x = Mathf.Clamp(_lastThumbDrawRect.x, 0f, Screen.width - 1f);
                float y = Mathf.Clamp(Screen.height - _lastThumbDrawRect.yMax, 0f, Screen.height - 1f);
                float w = Mathf.Min(_lastThumbDrawRect.width, Screen.width - x);
                float h = Mathf.Min(_lastThumbDrawRect.height, Screen.height - y);
                if (w <= 1f || h <= 1f) yield break;
                var tex = new Texture2D((int)w, (int)h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(x, y, w, h), 0, 0);
                tex.Apply();
                Color[] px = tex.GetPixels();
                float avg = 0f;
                for (int i = 0; i < px.Length; i++)
                {
                    avg += 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                }
                avg /= Mathf.Max(px.Length, 1);
                string msg = "[v3.0.12 Debug] 画面描画後のサムネ平均輝度=" + avg.ToString("F3") + " (読み込み値=" + LastThumbBrightness.ToString("F3") + ")";
                try
                {
                    string root = Path.GetDirectoryName(Application.dataPath);
                    File.AppendAllText(Path.Combine(root, "KK_SceneExplorer_brightness.log"), msg + Environment.NewLine);
                }
                catch { }
                SceneExplorerPlugin.Log.LogWarning(msg);
                Destroy(tex);
            }
            catch (Exception ex)
            {
                SceneExplorerPlugin.Log.LogWarning("[v3.0.12 Debug] 画面計測失敗: " + ex.Message);
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
            // プラグイン側のフィールドを更新（別タスクで実装）
            SceneExplorerPlugin.CurrentBrowserFolder = path;
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
