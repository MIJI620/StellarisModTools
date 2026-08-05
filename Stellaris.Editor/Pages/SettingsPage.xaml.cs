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

    public SettingsPage(EngineServices services)
    {
        _services = services;
        InitializeComponent();

        var loc = services.Localisation;
        NavDirs.Content = loc.Get("settings.roots");
        NavLang.Content = loc.Get("settings.language");
        NavMod.Content = loc.Get("settings.mod");
        NavHelp.Content = loc.Get("settings.help");

        NavList.SelectedIndex = 0;
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (NavList.SelectedIndex)
        {
            case 0: ContentHost.Content = BuildDirsPanel(); break;
            case 1: ContentHost.Content = BuildLangPanel(); break;
            case 2: ContentHost.Content = BuildModPanel(); break;
            default: ContentHost.Content = BuildHelpPanel(); break;
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
        var helpLines = new[]
        {
            "settings.help_roots",
            "settings.help_lang",
            "settings.help_style",
            "settings.help_map",
            "settings.help_export",
            "settings.help_save",
            "settings.help_normalize",
            "settings.help_profiles",
            "settings.help_sandbox"
        };
        foreach (var key in helpLines)
        {
            panel.Children.Add(new TextBlock
            {
                Text = loc.Get(key),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            });
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
        profileMenu.Items.Add(newProfile);
        profileMenu.Items.Add(renameProfile);
        profileMenu.Items.Add(reloadProfile);
        profileMenu.Items.Add(delProfile);
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
        var saveAsProfile = new MenuItem { Header = loc.Get("settings.profile_save_as") };
        saveAsProfile.Click += (_, _) => SaveCurrentAsProfile(list);
        var reloadItem = new MenuItem { Header = loc.Get("settings.reload") };
        reloadItem.Click += (_, _) => ReloadAll();
        menu.Items.Add(addItem);
        menu.Items.Add(removeItem);
        menu.Items.Add(saveAsProfile);
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

    private void RefreshProfileNav()
    {
        if (_profileList == null)
            return;
        _profileList.Items.Clear();
        // 无"当前目录"项——初始播放集叫 Default（旧配置已迁移）；选中 ActiveRootsProfile
        foreach (var name in _services.Preferences.RootsProfiles.Keys)
            _profileList.Items.Add(new ListBoxItem { Content = name, Tag = name });
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

    private void OnProfileSelected()
    {
        if (_profileList?.SelectedItem is not ListBoxItem it || it.Tag is not string name)
            return;
        _activeProfile = name;
        System.Diagnostics.Debug.WriteLine($"[Dirs] 切集合: {name}");
        if (_dirsList == null)
            return;
        _dirsList.Items.Clear();
        if (_services.Preferences.RootsProfiles.TryGetValue(name, out var profDirs))
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
        _services.Preferences.RootsProfiles[name] = new List<string>();
        _services.Preferences.ActiveRootsProfile = name;
        _activeProfile = name;
        _services.Preferences.Save();
        RefreshProfileNav();
        OnProfileSelected();
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
        _services.Preferences.ActiveRootsProfile = name;
        _activeProfile = name;
        _services.Preferences.Save();
        RefreshProfileNav();
    }

    /// <summary>
    /// 重载入：先把最新的路径列表保存到本地用户配置，
    /// 再按已有目录集从头重新扫描并重建主窗口（由 App.RestartFromRoots 完成，不再弹出选择窗口）。
    /// </summary>
    private void ReloadAll()
    {
        // 第一步：路径列表（增删/排序已实时保存）落盘兜底；激活集合时用集合目录作为加载 Roots
        if (!string.IsNullOrEmpty(_activeProfile)
            && _services.Preferences.RootsProfiles.TryGetValue(_activeProfile, out var profileDirs))
        {
            _services.Preferences.Roots.Clear();
            _services.Preferences.Roots.AddRange(profileDirs);
            _services.Preferences.ActiveRootsProfile = _activeProfile;
        }
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
        // 更新激活集合；Roots 同步 = 激活集合（加载/其他用 Roots 处保持一致）
        _services.Preferences.RootsProfiles[_activeProfile] = dirs;
        _services.Preferences.ActiveRootsProfile = _activeProfile;
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
