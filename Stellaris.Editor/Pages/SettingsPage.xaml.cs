// 文件: Stellaris.Editor/Pages/SettingsPage.xaml.cs
// 设置页（规范 5.1-c）：内部导航（目录 / 语言）+ 内容栏。
//   目录：撑满内容区、底部帮助行；右键新增/删除/重载入、拖拽上移/下移、
//         左键单选、Shift 连选、Ctrl 多选。
//   语言：左右表格布局（文本左对齐、控件右对齐；下拉按最长项定宽、
//         输入框按界面宽 1/4）。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace Stellaris.Editor.Pages;

public partial class SettingsPage : UserControl
{
    private readonly EngineServices _services;

    // 目录列表拖拽状态（支持多选组拖拽 + 插入线指示落点）
    private Point _dragStart;
    private readonly List<string> _dragItems = new();
    private Border? _dirsInsert;
    private ListBox? _dirsList;
    private ListBox? _profileList;
    private string? _activeProfile;   // 当前编辑的加载集合（null = 当前目录）
    private string? _loadedProfile;   // 本次启动实际加载的集合（SyncRoots 生效的快照——中括号标记跟它）

    public SettingsPage(EngineServices services)
    {
        _services = services;
        InitializeComponent();

        var loc = services.Localisation;
        NavDirs.Content = loc.Get("settings.roots");
        NavLang.Content = loc.Get("settings.language");
        NavMod.Content = loc.Get("settings.mod");
        NavHelp.Content = loc.Get("settings.help");
        NavAbout.Content = loc.Get("settings.about");

        NavList.SelectedIndex = 0;
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (NavList.SelectedIndex)
        {
            case 0: ContentHost.Content = BuildDirsPanel(); break;
            case 1: ContentHost.Content = BuildLangPanel(); break;
            case 2: ContentHost.Content = BuildModPanel(); break;
            case 3: ContentHost.Content = BuildHelpPanel(); break;
            default: ContentHost.Content = BuildAboutPanel(); break;
        }
    }

    // ==================== 帮助（功能说明） ====================

    private FrameworkElement BuildHelpPanel()
    {
        var loc = _services.Localisation;
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        var title = new TextBlock
        {
            Text = loc.Get("settings.help"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(title);
        // Markdown 渲染（MdXaml——用户 2026-08：帮助/关于都用 Markdown，改内容更方便）
        try
        {
            var md = new MdXaml.MarkdownScrollViewer
            {
                Markdown = loc.Get("settings.help_text"),
                Margin = new Thickness(0, 0, 0, 8)
            };
            EnableMarkdownLinks(md);
            panel.Children.Add(md);
        }
        catch (Exception ex)
        {
            try { Stellaris.Parser.LoggerSetup.GetFactory()
                .CreateLogger("SettingsHelp")
                .LogError(ex, "MdXaml 帮助渲染失败，回退文本块"); } catch { }
            // 回退：大文本块（MdXaml 渲染失败时仍可读）
            panel.Children.Add(new TextBlock
            {
                Text = loc.Get("settings.help_text"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }
        return new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ==================== 关于 ====================

    /// <summary>MdXaml 渲染的链接用 WPF 标准命令 NavigationCommands.GoToPage（URL 在 CommandParameter，NavigateUri 空）。
    /// 无 Frame 处理时点击无效（用户 2026-08：链接点击无效、光标为文本输入模式）。
    /// 修复：给控件挂 CommandBinding(GoToPage) 处理打开浏览器（命令路由，不依赖遍历时机）+ 遍历 Hyperlink 挂 Click 兜底。</summary>
    private static void EnableMarkdownLinks(MdXaml.MarkdownScrollViewer md)
    {
        md.CommandBindings.Add(new System.Windows.Input.CommandBinding(
            System.Windows.Input.NavigationCommands.GoToPage,
            (_, e) => OpenLink(e.Parameter?.ToString())));

        var wired = new System.Collections.Generic.HashSet<System.Windows.Documents.Hyperlink>();
        void TryWire()
        {
            try
            {
                if (md.Document is not System.Windows.Documents.FlowDocument doc)
                    return;
                foreach (var b in doc.Blocks)
                    WalkBlock(b);
            }
            catch { /* 链接处理失败不影响显示 */ }
        }
        md.Loaded += (_, _) => TryWire();
        md.LayoutUpdated += (_, _) => TryWire();   // 异步渲染完成后 Document 更新 → 补挂

        void WalkBlock(System.Windows.Documents.Block b)
        {
            switch (b)
            {
                case System.Windows.Documents.Paragraph p:
                    foreach (var i in p.Inlines)
                        WalkInline(i);
                    break;
                case System.Windows.Documents.List l:
                    foreach (var li in l.ListItems)
                        foreach (var lb in li.Blocks)
                            WalkBlock(lb);
                    break;
                case System.Windows.Documents.Section s:
                    foreach (var sb in s.Blocks)
                        WalkBlock(sb);
                    break;
                case System.Windows.Documents.Table t:
                    foreach (var rg in t.RowGroups)
                        foreach (var row in rg.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var cb in cell.Blocks)
                                    WalkBlock(cb);
                    break;
            }
        }

        void WalkInline(System.Windows.Documents.Inline i)
        {
            if (i is System.Windows.Documents.Hyperlink h)
            {
                if (!wired.Add(h))
                    return;
                h.Click += (_, _) => OpenLink(h.CommandParameter?.ToString() ?? h.NavigateUri?.ToString());
                h.RequestNavigate += (_, e) => OpenLink(e.Uri?.ToString() ?? h.NavigateUri?.ToString());
            }
            else if (i is System.Windows.Documents.Span sp)
            {
                foreach (var c in sp.Inlines)
                    WalkInline(c);
            }
        }

        static void OpenLink(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private FrameworkElement BuildAboutPanel()
    {
        var loc = _services.Localisation;
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = loc.Get("settings.about"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 8)
        });
        // 内容硬编码（用户 2026-08：不用本地化）；Markdown 渲染（MdXaml），失败回退文本块
        const string aboutMd =
            "**群星模组工具（StellarisModTools）V0.2** —— 开源免费的群星（Stellaris 4.x）模组可视化编辑工具：星系样式、地图、法令/决议/静态加成、科技、索引、本地化一站式编辑。\n\n" +
            "GitHub：[StellarisModTools](https://github.com/MIJI620/StellarisModTools)\n\n" +
            "## 开发历程\n\n" +
            "本工具的最初开发源自 2021 年左右的一个群星舰船制作器——当时只是想做一个便捷的可视化舰船组件制作工具，后因长期未维护且性能过差而停更；2026 年重新开始开发，先尝试用 Python，再次遇到性能瓶颈，最终转向 C#。感谢一路以来的支持。\n\n" +
            "## 关于本工具\n\n" +
            "- 完全开源免费：代码公开，可自由学习、修改与二次开发；\n" +
            "- 分享：欢迎分享给需要的朋友；禁止完全原样打包倒卖（改动任何内容即不受限，详见 LICENSE）。\n\n" +
            "## 使用限制\n\n" +
            "- 本工具为辅助编辑工具，不修改游戏本体文件，仅写 mod 目录（roots 最后一位）；\n" +
            "- 所有保存必须由你显式触发（右键\"保存\"），不会自动落盘；\n" +
            "- 如遇异常，可查看日志 editor_debug.log / error.log 反馈。\n\n" +
            "## 权限与作者\n\n" +
            "- 作者：MIJI\n" +
            "- 感谢你的使用与反馈！";
        try
        {
            var md = new MdXaml.MarkdownScrollViewer { Markdown = aboutMd };
            EnableMarkdownLinks(md);
            panel.Children.Add(md);
        }
        catch
        {
            panel.Children.Add(new TextBlock { Text = aboutMd, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        }
        return new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ==================== 模组设置（模组前缀） ====================

    private FrameworkElement BuildModPanel()
    {
        var loc = _services.Localisation;
        var modPrefs = _services.ModPrefs ?? new ModPreferences();
        string modRoot = _services.Preferences.Roots.Count > 0
            ? _services.Preferences.Roots[^1]
            : string.Empty;

        var label = new TextBlock
        {
            Text = loc.Get("settings.mod_prefix"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        var box = new TextBox
        {
            Text = modPrefs.ModPrefix,
            MinWidth = 180,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.LostFocus += (_, _) =>
        {
            string prefix = (box.Text ?? string.Empty).Trim();
            if (prefix.Length == 0)
            {
                box.Text = modPrefs.ModPrefix;
                return;
            }
            modPrefs.ModPrefix = prefix;
            if (!modPrefs.Save(modRoot))
            {
                MessageBox.Show("保存模组设置失败（模组目录不可写？）", "Stellaris Mod Tools",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var hint = new TextBlock
        {
            Text = loc.Get("settings.mod_prefix_hint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var row = new DockPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };
        row.Children.Add(label);
        row.Children.Add(box);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(row);
        panel.Children.Add(hint);

        // ---- 启用语言：勾选的语言才会在新增样式时自动生成本地化，本地化编辑区可选 ----
        var langLabel = new TextBlock
        {
            Text = loc.Get("settings.enabled_languages"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 16, 0, 4)
        };
        panel.Children.Add(langLabel);

        var langHint = new TextBlock
        {
            Text = loc.Get("settings.enabled_languages_hint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 6)
        };
        panel.Children.Add(langHint);

        // 可用语言 = 模组当前本地化语言（空则兜底 english）
        var availableLangs = _services.Adapter?.GetAllLocalisations().Keys.ToList() ?? new List<string>();
        if (availableLangs.Count == 0)
            availableLangs.Add("english");

        // 已启用集合：读模组偏好（ModPreferences.EnabledLanguages，与 ModPrefix 同级）；未设置 → 全部启用
        var enabledSet = new HashSet<string>(StringComparer.Ordinal);
        if (modPrefs?.EnabledLanguages is { Count: > 0 } langs2)
        {
            foreach (var s in langs2)
                enabledSet.Add(s);
        }
        bool allEnabled = enabledSet.Count == 0;

        var langChecks = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        foreach (var l in availableLangs)
        {
            var cb = new CheckBox
            {
                Content = l,
                IsChecked = allEnabled || enabledSet.Contains(l),
                Margin = new Thickness(16, 2, 0, 2)
            };
            cb.Checked += (_, _) => SaveEnabledLanguages(langChecks);
            cb.Unchecked += (_, _) => SaveEnabledLanguages(langChecks);
            langChecks[l] = cb;
            panel.Children.Add(cb);
        }

        // ---- 点精度：静态地图星系坐标精确到几位小数（银河类别 galaxy.json）----
        var precisionLabel = new TextBlock
        {
            Text = loc.Get("settings.point_precision"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 16, 0, 4)
        };
        panel.Children.Add(precisionLabel);
        var precisionHint = new TextBlock
        {
            Text = loc.Get("settings.point_precision_hint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 12, 6)
        };
        panel.Children.Add(precisionHint);
        var precisionCombo = new ComboBox
        {
            Margin = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        for (int i = 0; i <= 3; i++)
            precisionCombo.Items.Add(new ComboBoxItem { Content = loc.Get($"settings.point_precision_{i}"), Tag = i });
        int curPrecision = 1;
        var pcm = _services.ConfigManager;
        if (pcm != null)
        {
            try
            {
                var pv = pcm.Get("galaxy", "global.behavior.point_precision");
                // Get 返回 CLR 对象（ConvertNodeToObject：JsonValue → int/bool/...）
                if (pv is int pi)
                    curPrecision = Math.Clamp(pi, 0, 3);
                else if (pv is long pl)
                    curPrecision = Math.Clamp((int)pl, 0, 3);
                else
                    Stellaris.Parser.LoggerSetup.GetFactory().CreateLogger("Settings")
                        .LogWarning("点精度读取：pv 类型 {Type}", pv?.GetType().FullName);
            }
            catch (Exception ex)
            {
                // 诊断：读取异常（键不存在 / 类别不存在 / 解析失败）
                Stellaris.Parser.LoggerSetup.GetFactory().CreateLogger("Settings")
                    .LogError(ex, "读取点精度失败");
            }
        }
        foreach (object o in precisionCombo.Items)
        {
            if (o is ComboBoxItem it && it.Tag is int tag && tag == curPrecision)
            {
                precisionCombo.SelectedItem = it;
                break;
            }
        }
        precisionCombo.SelectionChanged += (_, _) =>
        {
            if (precisionCombo.SelectedItem is ComboBoxItem it2 && it2.Tag is int v)
            {
                try
                {
                    pcm?.SetBatch("galaxy", new Dictionary<string, object>
                    {
                        ["global.behavior.point_precision"] = v
                    });
                }
                catch
                {
                    // 写入失败忽略
                }
            }
        };
        panel.Children.Add(precisionCombo);

        return panel;
    }

    /// <summary>把勾选的"启用语言"写入银河类别 galaxy.json（global.behavior.enabled_languages）。</summary>
    private void SaveEnabledLanguages(Dictionary<string, CheckBox> langChecks)
    {
        var modPrefs = _services.ModPrefs;
        if (modPrefs == null)
            return;
        var selected = langChecks.Where(kv => kv.Value.IsChecked == true)
                                 .Select(kv => kv.Key)
                                 .OrderBy(x => x, StringComparer.Ordinal)
                                 .ToList();
        modPrefs.EnabledLanguages = selected;
        string modRoot = _services.Roots.Count > 0 ? _services.Roots[^1] : string.Empty;
        if (!string.IsNullOrEmpty(modRoot))
            modPrefs.Save(modRoot);
    }

    // ==================== 目录面板 ====================

    private FrameworkElement BuildDirsPanel()
    {
        var loc = _services.Localisation;

        // ---- 左：加载集合导航（一列）----
        var profileList = new ListBox { Margin = new Thickness(8, 8, 4, 4) };
        _profileList = profileList;
        var profileMenu = new ContextMenu();
        var newProfile = new MenuItem { Header = loc.Get("settings.profile_new") };
        newProfile.Click += (_, _) => NewProfile();
        var delProfile = new MenuItem { Header = loc.Get("settings.profile_delete") };
        delProfile.Click += (_, _) => DeleteProfile();
        var renameProfile = new MenuItem { Header = loc.Get("settings.profile_rename") };
        renameProfile.Click += (_, _) => RenameProfile();
        var reloadProfile = new MenuItem { Header = loc.Get("settings.profile_reload") };
        reloadProfile.Click += (_, _) => ReloadAll();
        var importLauncher = new MenuItem { Header = loc.Get("settings.profile_import_launcher") };
        importLauncher.Click += (_, _) => ImportLauncherSets();
        profileMenu.Items.Add(newProfile);
        profileMenu.Items.Add(renameProfile);
        profileMenu.Items.Add(reloadProfile);
        profileMenu.Items.Add(delProfile);
        profileMenu.Items.Add(new Separator());   // 导入启动器集合移到最下面 + 分隔线（用户 2026-08）
        profileMenu.Items.Add(importLauncher);
        profileList.ContextMenu = profileMenu;
        profileList.SelectionChanged += (_, _) => OnProfileSelected();
        RefreshProfileNav();
        var profileTitle = new TextBlock
        {
            Text = loc.Get("settings.profile"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 8, 0, 0)
        };

        // ---- 右：目录列表 ----
        var list = new ListBox
        {
            Margin = new Thickness(8, 8, 8, 4),
            SelectionMode = SelectionMode.Extended, // 左键单选 / Shift 连选 / Ctrl 多选
            AllowDrop = true
        };
        list.PreviewMouseLeftButtonDown += OnDirsMouseDown;
        list.PreviewMouseMove += OnDirsMouseMove;
        list.DragOver += OnDirsDragOver;
        list.DragLeave += OnDirsDragLeave;
        list.Drop += OnDirsDrop;
        _dirsList = list;

        // 拖拽插入线指示（覆盖在列表上，告知拖到哪个元素之前/之后）
        var insert = new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xD9)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 0, 8, 0)
        };
        _dirsInsert = insert;

        // 右键菜单：新增 / 删除 / 重载入
        var menu = new ContextMenu();
        var addItem = new MenuItem { Header = loc.Get("roots.add") };
        addItem.Click += (_, _) => AddDirectories(list);
        var removeItem = new MenuItem { Header = loc.Get("roots.remove") };
        removeItem.Click += (_, _) => RemoveSelected(list);
        var pinTopItem = new MenuItem { Header = loc.Get("settings.profile_pin_top") };
        pinTopItem.Click += (_, _) => PinSelectedToTop(list);
        var saveAsProfile = new MenuItem { Header = loc.Get("settings.profile_save_as") };
        saveAsProfile.Click += (_, _) => SaveCurrentAsProfile(list);
        var gameMarkItem = new MenuItem();
        menu.Opened += (_, _) =>
        {
            var sel = list.SelectedItem?.ToString();
            bool isGame = sel != null
                && string.Equals(_services.Preferences.GameRoot, FullPathOf(sel), StringComparison.OrdinalIgnoreCase);
            gameMarkItem.Header = isGame ? loc.Get("settings.game_unmark") : loc.Get("settings.game_mark");
            gameMarkItem.IsEnabled = sel != null;
        };
        gameMarkItem.Click += (_, _) =>
        {
            var sel = list.SelectedItem?.ToString();
            if (sel == null) return;
            // 全局唯一：标记新游戏 root（旧的自动替换）
            _services.Preferences.GameRoot = FullPathOf(sel);
            _services.Preferences.Save();
        };
        var reloadItem = new MenuItem { Header = loc.Get("settings.reload") };
        reloadItem.Click += (_, _) => ReloadAll();
        menu.Items.Add(addItem);
        menu.Items.Add(removeItem);
        menu.Items.Add(pinTopItem);
        menu.Items.Add(saveAsProfile);
        menu.Items.Add(gameMarkItem);
        menu.Items.Add(reloadItem);
        list.ContextMenu = menu;

        foreach (var root in _services.Roots)
            list.Items.Add(root);

        var hint = new TextBlock
        {
            Text = loc.Get("settings.roots_hint"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)),
            Margin = new Thickness(8, 0, 8, 8),
            TextWrapping = TextWrapping.Wrap
        };

        // 列表撑满剩余高度，底部留一行帮助；插入线覆盖在列表之上
        var listHost = new Grid();
        listHost.Children.Add(list);
        listHost.Children.Add(insert);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(listHost, 0);
        Grid.SetRow(hint, 1);
        grid.Children.Add(listHost);
        grid.Children.Add(hint);

        // 双栏：左集合导航 + 右目录编辑
        var profileHost = new DockPanel();
        DockPanel.SetDock(profileTitle, Dock.Top);
        profileHost.Children.Add(profileTitle);
        profileHost.Children.Add(profileList);
        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.Children.Add(profileHost);
        main.Children.Add(grid);
        Grid.SetColumn(grid, 1);
        return main;
    }

    private static string FullPathOf(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch { return path; }
    }

    private void RefreshProfileNav()
    {
        if (_profileList == null)
            return;
        _profileList.Items.Clear();
        // 无"当前目录"项——初始播放集叫 Default（旧配置已迁移）；选中 ActiveRootsProfile
        // 正在被加载的集合用中括号标记（[名]）——跟"启动时实际加载的"（快照），
        // 切换集合（未重载）不改变中括号；重载后页面重建 → 新快照
        _loadedProfile ??= _services.Preferences.ActiveRootsProfile;
        var active = _loadedProfile;
        foreach (var name in _services.Preferences.RootsProfiles.Keys)
        {
            var display = string.Equals(name, active, StringComparison.OrdinalIgnoreCase)
                ? "[" + name + "]"
                : name;
            _profileList.Items.Add(new ListBoxItem { Content = display, Tag = name });
        }
        foreach (object o in _profileList.Items)
        {
            if (o is ListBoxItem it && (it.Tag as string) == _activeProfile)
            {
                _profileList.SelectedItem = it;
                break;
            }
        }
        if (_profileList.SelectedItem == null && _profileList.Items.Count > 0)
            _profileList.SelectedIndex = 0;
    }

    /// <summary>只更新导航项的中括号标记（不重建 Items——避免递归触发 SelectionChanged）。</summary>
    private void RefreshProfileBrackets()
    {
        if (_profileList == null)
            return;
        var active = _loadedProfile;
        foreach (object o in _profileList.Items)
        {
            if (o is ListBoxItem it && it.Tag is string name)
                it.Content = string.Equals(name, active, StringComparison.OrdinalIgnoreCase)
                    ? "[" + name + "]"
                    : name;
        }
    }

    private void OnProfileSelected()
    {
        if (_profileList?.SelectedItem is not ListBoxItem it || it.Tag is not string name)
            return;
        _activeProfile = name;
        System.Diagnostics.Debug.WriteLine($"[Dirs] 切集合: {name}");
        // 仅切换编辑目标：Roots 工作区同步为集合目录（**活集合不变**——活集合只在重载入环节调整）
        if (_services.Preferences.RootsProfiles.TryGetValue(name, out var profDirs))
        {
            _services.Preferences.Roots.Clear();
            _services.Preferences.Roots.AddRange(profDirs);
            _services.Preferences.Save();
            // 只刷新中括号标记（不重建 Items——重建会重设 SelectedItem → 递归触发本方法 → StackOverflow）
            RefreshProfileBrackets();
        }
        if (_dirsList == null)
            return;
        _dirsList.Items.Clear();
        if (_services.Preferences.RootsProfiles.TryGetValue(name, out profDirs))
        {
            foreach (var dir in profDirs)
                _dirsList.Items.Add(dir);
        }
    }

    private void NewProfile()
    {
        var win = new Window
        {
            Title = _services.Localisation.Get("settings.profile_new"),
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var box = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(new TextBlock { Text = _services.Localisation.Get("settings.profile_name") });
        panel.Children.Add(box);
        var ok = new Button
        {
            Content = _services.Localisation.Get("common.ok"),
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        if (win.ShowDialog() != true || string.IsNullOrWhiteSpace(box.Text))
            return;
        var name = box.Text.Trim();
        // 新建集合默认为空（不复制当前集合内容——用户要求）；"保存为加载集合"才显式复制当前目录
        // 不切活集合（活集合只在重载入环节调整）——右键新合集"重载入集合"才生效
        _services.Preferences.RootsProfiles[name] = new List<string>();
        _activeProfile = name;
        _services.Preferences.Save();
        RefreshProfileNav();
        OnProfileSelected();
    }

    /// <summary>导入启动器数据库（launcher-v2.sqlite）：每个播放集生成一个加载集合（enabled mod 目录按加载顺序）。</summary>
    private void ImportLauncherSets()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = _services.Localisation.Get("settings.profile_import_launcher"),
            Filter = "启动器数据库 (*.sqlite)|*.sqlite|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            var sets = Stellaris.Parser.LauncherSqliteImporter.Import(dlg.FileName);
            if (sets.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    _services.Localisation.Get("settings.profile_import_empty"),
                    _services.Localisation.Get("common.error"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            string? firstImported = null;
            foreach (var s in sets)
            {
                var name = s.Name;
                var used = _services.Preferences.RootsProfiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (used.Contains(name))
                {
                    var i = 2;
                    while (used.Contains(name + " (" + i + ")"))
                        i++;
                    name += " (" + i + ")";
                }
                _services.Preferences.RootsProfiles[name] = s.ModDirs;
                firstImported ??= name;
            }
            _services.Preferences.Save();
            RefreshProfileNav();
            if (firstImported != null)
            {
                // 仅设编辑目标，不切活集合（活集合只在重载入环节调整）
                _activeProfile = firstImported;
                OnProfileSelected();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                string.Format(_services.Localisation.Get("settings.profile_import_failed"), ex.Message),
                _services.Localisation.Get("common.error"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void RenameProfile()
    {
        if (_activeProfile == null)
            return;
        var win = new Window
        {
            Title = _services.Localisation.Get("settings.profile_rename"),
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var box = new TextBox { Text = _activeProfile, Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(new TextBlock { Text = _services.Localisation.Get("settings.profile_name") });
        panel.Children.Add(box);
        var ok = new Button
        {
            Content = _services.Localisation.Get("common.ok"),
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        if (win.ShowDialog() != true || string.IsNullOrWhiteSpace(box.Text))
            return;
        var newName = box.Text.Trim();
        if (newName == _activeProfile || _services.Preferences.RootsProfiles.ContainsKey(newName))
            return;
        var dirs = _services.Preferences.RootsProfiles[_activeProfile];
        _services.Preferences.RootsProfiles.Remove(_activeProfile);
        _services.Preferences.RootsProfiles[newName] = dirs;
        // 仅活集合指向被重命名的合集时才跟随改名（否则活集合被偷偷切走）
        if (string.Equals(_services.Preferences.ActiveRootsProfile, _activeProfile, StringComparison.OrdinalIgnoreCase))
            _services.Preferences.ActiveRootsProfile = newName;
        _activeProfile = newName;
        _services.Preferences.Save();
        RefreshProfileNav();
    }

    private void DeleteProfile()
    {
        if (_activeProfile == null)
            return;
        _services.Preferences.RootsProfiles.Remove(_activeProfile);
        // 仅活集合指向被删除的合集时才清空（否则活集合被偷偷清掉）
        if (string.Equals(_services.Preferences.ActiveRootsProfile, _activeProfile, StringComparison.OrdinalIgnoreCase))
            _services.Preferences.ActiveRootsProfile = null;
        _activeProfile = null;
        _services.Preferences.Save();
        RefreshProfileNav();
        OnProfileSelected();
    }

    private void SaveCurrentAsProfile(ListBox list)
    {
        var win = new Window
        {
            Title = _services.Localisation.Get("settings.profile_save_as"),
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var box = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(new TextBlock { Text = _services.Localisation.Get("settings.profile_name") });
        panel.Children.Add(box);
        var ok = new Button
        {
            Content = _services.Localisation.Get("common.ok"),
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) => win.DialogResult = true;
        panel.Children.Add(ok);
        win.Content = panel;
        if (win.ShowDialog() != true || string.IsNullOrWhiteSpace(box.Text))
            return;
        var name = box.Text.Trim();
        var dirs = list.Items.Cast<object>().Select(x => x.ToString()!).ToList();
        _services.Preferences.RootsProfiles[name] = dirs;
        // 不切活集合（活集合只在重载入环节调整）——右键新合集"重载入集合"才生效
        _activeProfile = name;
        _services.Preferences.Save();
        RefreshProfileNav();
    }

    /// <summary>
    /// 重载入：把**当前 Roots**（用户可能增删/排序过）写回激活合集——
    /// 重载入后该合集即为下次启动载入的合集；再按现有目录集从头重新扫描并重建主窗口。
    /// </summary>
    private void ReloadAll()
    {
        // 当前 Roots 写回激活合集（无激活/无合集 → 建 Default）
        string active = _activeProfile;
        if (string.IsNullOrEmpty(active) || !_services.Preferences.RootsProfiles.ContainsKey(active))
        {
            if (_services.Preferences.RootsProfiles.Count == 0)
            {
                active = "Default";
                _services.Preferences.RootsProfiles["Default"] = new List<string>();
            }
            else
            {
                active = _services.Preferences.RootsProfiles.Keys.First();
            }
            _activeProfile = active;
        }
        _services.Preferences.ActiveRootsProfile = active;
        _services.Preferences.RootsProfiles[active] = new List<string>(_services.Preferences.Roots);
        _services.Preferences.Save();

        // 第二步~第三步：关闭主窗口 → 按现有目录集从头扫描并重建主窗口
        if (Application.Current is App app)
            app.RestartFromRoots();
    }

    private void AddDirectories(ListBox list)
    {
        var dlg = new OpenFolderDialog { Multiselect = true };
        if (dlg.ShowDialog() != true)
            return;
        foreach (var folder in dlg.FolderNames)
        {
            if (!list.Items.Contains(folder))
                list.Items.Add(folder);
        }
        CommitRoots(list);
    }

    private void RemoveSelected(ListBox list)
    {
        var selected = new List<object>();
        foreach (var item in list.SelectedItems)
            selected.Add(item);
        foreach (var item in selected)
            list.Items.Remove(item);
        CommitRoots(list);
    }

    /// <summary>把选中的目录置顶（保持选中项相对顺序——用于快速把游戏文件夹挪到首位）。</summary>
    private void PinSelectedToTop(ListBox list)
    {
        var selected = new List<object>();
        foreach (var item in list.SelectedItems)
            selected.Add(item);
        if (selected.Count == 0)
            return;
        foreach (var item in selected)
            list.Items.Remove(item);
        // 逆序插入到最前（保持原相对顺序）
        for (int i = selected.Count - 1; i >= 0; i--)
            list.Items.Insert(0, selected[i]);
        CommitRoots(list);
    }

    private void OnDirsMouseDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        _dragStart = e.GetPosition(list);
        var container = list.ContainerFromElement(e.OriginalSource as DependencyObject);
        var item = (container as ListBoxItem)?.Content as string;

        _dragItems.Clear();
        if (item != null && list.SelectedItems.Contains(item))
        {
            // 点击已选中项 → 拖拽整个选中组
            foreach (var si in list.SelectedItems)
                if (si is string s)
                    _dragItems.Add(s);
        }
        else if (item != null)
        {
            _dragItems.Add(item);
        }
    }

    private void OnDirsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItems.Count == 0)
            return;
        var list = (ListBox)sender;
        var pos = e.GetPosition(list);
        if (Math.Abs(pos.X - _dragStart.X) < 5 && Math.Abs(pos.Y - _dragStart.Y) < 5)
            return;
        DragDrop.DoDragDrop(list, new List<string>(_dragItems), DragDropEffects.Move);
        _dragItems.Clear();
    }

    private void OnDirsDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(List<string>)) is List<string> drags && drags.Count > 0)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            ShowInsertIndicator(GetDropIndex((ListBox)sender, e.GetPosition((ListBox)sender)));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnDirsDragLeave(object sender, DragEventArgs e)
    {
        if (_dirsInsert != null)
            _dirsInsert.Visibility = Visibility.Collapsed;
    }

    /// <summary>在指定插入位置显示标记线（两个元素之间 / 某项前 / 某项后）。</summary>
    private void ShowInsertIndicator(int index)
    {
        var list = _dirsList;
        if (list == null || _dirsInsert == null)
            return;
        double y;
        if (index >= 0 && index < list.Items.Count)
        {
            var container = list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            y = container != null
                ? container.TransformToAncestor(list).Transform(new Point(0, 0)).Y
                : 0;
        }
        else
        {
            var last = list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) as ListBoxItem;
            y = last != null
                ? last.TransformToAncestor(list).Transform(new Point(0, 0)).Y + last.ActualHeight
                : list.ActualHeight - 2;
        }
        _dirsInsert.Margin = new Thickness(8, Math.Max(0, y - 1) + list.Margin.Top, 8, 0);
        _dirsInsert.Visibility = Visibility.Visible;
    }

    private void OnDirsDrop(object sender, DragEventArgs e)
    {
        if (_dirsInsert != null)
            _dirsInsert.Visibility = Visibility.Collapsed;
        var list = (ListBox)sender;
        if (e.Data.GetData(typeof(List<string>)) is not List<string> dragged || dragged.Count == 0)
            return;

        int target = GetDropIndex(list, e.GetPosition(list));

        // 记录组内各项原索引，移除后修正插入点
        var originalIndex = new Dictionary<string, int>();
        for (int i = 0; i < list.Items.Count; i++)
            if (list.Items[i] is string s)
                originalIndex[s] = i;
        int before = 0;
        foreach (var d in dragged)
            if (originalIndex.TryGetValue(d, out var idx) && idx < target)
                before++;

        foreach (var item in dragged)
            list.Items.Remove(item);

        int insertAt = Math.Max(0, target - before);
        for (int i = 0; i < dragged.Count; i++)
            list.Items.Insert(Math.Min(insertAt + i, list.Items.Count), dragged[i]);

        list.SelectedItem = dragged[^1];
        CommitRoots(list);
    }

    private static int GetDropIndex(ListBox list, Point pos)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            var container = list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container == null) continue;
            if (pos.Y < container.TransformToAncestor(list).Transform(new Point(0, 0)).Y
                           + container.ActualHeight / 2)
                return i;
        }
        return list.Items.Count;
    }

    private void CommitRoots(ListBox list)
    {
        var dirs = list.Items.Cast<object>().Select(x => x.ToString()!).ToList();
        if (string.IsNullOrEmpty(_activeProfile))
            return;
        // 仅写回当前编辑合集；Roots 工作区同步（**活集合不变**——活集合只在重载入环节调整）
        _services.Preferences.RootsProfiles[_activeProfile] = dirs;
        _services.Preferences.Roots.Clear();
        _services.Preferences.Roots.AddRange(dirs);
        _services.Preferences.Save();
    }

    // ==================== 语言面板（左右表格：文本左对齐、控件右对齐） ====================

    private FrameworkElement BuildLangPanel()
    {
        var loc = _services.Localisation;

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12) };

        // 第 1 行：重新载入（应用所选语言并刷新整个界面，靠右上角）
        var reloadButton = new Button
        {
            Content = loc.Get("settings.reload_ui"),
            Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        reloadButton.Click += (_, _) =>
        {
            if (Application.Current.MainWindow is MainWindow main)
                main.RefreshUIAfterLanguageChange();
        };
        panel.Children.Add(reloadButton);

        // 第 2 行：界面语言（下拉，自称、按最长项实际像素定宽）
        panel.Children.Add(BuildTableRow(
            loc.Get("settings.language") + ":",
            BuildLanguageCombo(loc),
            isCombo: true, longestText: null));

        // 字体（下拉，按最长项定宽）
        panel.Children.Add(BuildTableRow(
            loc.Get("settings.font") + ":",
            BuildFontCombo(),
            isCombo: true, longestText: null));

        // 字号（输入框，宽 = 界面宽 1/4）
        var sizeBox = new TextBox
        {
            Text = _services.Preferences.FontSize.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        sizeBox.LostFocus += (_, _) =>
        {
            if (double.TryParse(sizeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double size)
                && size > 4 && size < 100)
            {
                _services.Preferences.FontSize = size;
                _services.Preferences.Save();
                if (Application.Current.MainWindow is MainWindow main)
                    main.ApplyUserFont();
            }
            else
            {
                sizeBox.Text = _services.Preferences.FontSize.ToString(CultureInfo.InvariantCulture);
            }
        };
        var sizeRow = BuildTableRow(loc.Get("settings.font_size") + ":", sizeBox, isCombo: false, longestText: null);
        // 输入框宽 = 面板宽 × 1/4
        panel.SizeChanged += (_, _) =>
        {
            if (panel.ActualWidth > 0)
                sizeBox.Width = Math.Max(60, panel.ActualWidth * 0.25);
        };
        panel.Children.Add(sizeRow);

        // 科技卡片最小宽度 / 最小高度（用户设置；科技图布局用，重新打开科技页生效）
        var cardWBox = new TextBox
        {
            Text = _services.Preferences.TechCardMinWidth.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        cardWBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(cardWBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) && w >= 200)
            {
                _services.Preferences.TechCardMinWidth = w;
                _services.Preferences.Save();
            }
            else
            {
                cardWBox.Text = _services.Preferences.TechCardMinWidth.ToString(CultureInfo.InvariantCulture);
            }
        };
        panel.Children.Add(BuildTableRow(loc.Get("settings.tech_card_width") + ":", cardWBox, isCombo: false, longestText: null));
        var cardHBox = new TextBox
        {
            Text = _services.Preferences.TechCardMinHeight.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        cardHBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(cardHBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) && h >= 80)
            {
                _services.Preferences.TechCardMinHeight = h;
                _services.Preferences.Save();
            }
            else
            {
                cardHBox.Text = _services.Preferences.TechCardMinHeight.ToString(CultureInfo.InvariantCulture);
            }
        };
        panel.Children.Add(BuildTableRow(loc.Get("settings.tech_card_height") + ":", cardHBox, isCombo: false, longestText: null));
        // 两输入框宽 = 面板宽 × 1/4（与字号一致）
        panel.SizeChanged += (_, _) =>
        {
            if (panel.ActualWidth > 0)
            {
                cardWBox.Width = Math.Max(60, panel.ActualWidth * 0.25);
                cardHBox.Width = Math.Max(60, panel.ActualWidth * 0.25);
            }
        };

        return panel;
    }

    /// <summary>构建一行表格：文本左对齐、控件右对齐。</summary>
    private static FrameworkElement BuildTableRow(string labelText, FrameworkElement control,
        bool isCombo, string? longestText)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = labelText,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private ComboBox BuildLanguageCombo(UILocalisationManager loc)
    {
        var box = new ComboBox();
        double longestWidth = 80;
        foreach (var lang in loc.AvailableLanguages)
        {
            string display = loc.GetLanguageDisplayName(lang);
            var item = new ComboBoxItem { Content = display, Tag = lang };
            box.Items.Add(item);
            // 按实际像素宽度选最长项（自称含中文/西文混合，字符数不能代表宽度）
            double w = MeasureTextWidth(display);
            if (w > longestWidth) longestWidth = w;
            if (string.Equals(lang, loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                box.SelectedItem = item;
        }
        box.Width = longestWidth + 30;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem item || item.Tag is not string lang
                || lang == loc.CurrentLanguage)
                return;
            try
            {
                loc.SetLanguage(lang);
                _services.Preferences.Language = lang;
                _services.Preferences.Save();
            }
            catch
            {
                // 防御性
            }
        };
        return box;
    }

    private ComboBox BuildFontCombo()
    {
        var box = new ComboBox { MaxWidth = 320 };
        string longest = string.Empty;
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var item = new ComboBoxItem { Content = family.Source, Tag = family.Source };
            box.Items.Add(item);
            if (family.Source.Length > longest.Length) longest = family.Source;
            if (family.Source == _services.Preferences.FontFamily)
                box.SelectedItem = item;
        }
        if (box.SelectedItem == null && box.Items.Count > 0)
            box.SelectedIndex = 0;
        box.Width = Math.Min(320, MeasureTextWidth(longest) + 28);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem fItem || fItem.Tag is not string family)
                return;
            _services.Preferences.FontFamily = family;
            _services.Preferences.Save();
            if (Application.Current.MainWindow is MainWindow main)
                main.ApplyUserFont();
        };
        return box;
    }

    private double MeasureTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 80;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(_services.Preferences.FontFamily), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal),
            12, Brushes.Black, 1.0);
        return ft.Width;
    }
}
