// 文件: Stellaris.Editor/RootsWindow.xaml.cs
// Roots 多选界面（规范 4.2）：多选模组根目录，顺序即优先级（末尾最高）。
// 界面文本经本地化模块（规范 2.3）。

using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Stellaris.Editor;

/// <summary>Roots 选择窗口：确认后写入偏好设置并返回 true。</summary>
public partial class RootsWindow : Window
{
    private readonly EngineServices _services;

    private Action? _onConfirmed;

    /// <summary>设置"确认时"回调（App 用于先显示加载浮窗再关闭本窗口）。</summary>
    public void SetOnConfirmed(Action onConfirmed) => _onConfirmed = onConfirmed;

    public RootsWindow(EngineServices services)
    {
        _services = services;
        InitializeComponent();

        var loc = services.Localisation;
        Title = loc.Get("roots.title");
        HintText.Text = loc.Get("roots.hint");
        OkButton.Content = loc.Get("roots.ok");
        CancelButton.Content = loc.Get("roots.cancel");

        // 预填偏好中的根目录
        foreach (var root in services.Roots)
            RootsList.Items.Add(root);

        // 初始界面语种选择：自称（endonym）显示，切换即改界面语言并持久化
        var locSvc = services.Localisation;
        LangLabel.Text = locSvc.Get("roots.language");
        ProfileLabel.Text = locSvc.Get("roots.profile");
        RefreshProfileBox();
        foreach (var lang in locSvc.AvailableLanguages)
        {
            var item = new ComboBoxItem { Content = locSvc.GetLanguageDisplayName(lang), Tag = lang };
            LangBox.Items.Add(item);
            if (string.Equals(lang, locSvc.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                LangBox.SelectedItem = item;
        }
    }

    private void RefreshProfileBox()
    {
        ProfileBox.Items.Clear();
        // 无"（当前目录）"——初始播放集叫 Default（旧配置已迁移）；仅列出集合
        foreach (var name in _services.Preferences.RootsProfiles.Keys)
            ProfileBox.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        var active = _services.Preferences.ActiveRootsProfile;
        foreach (object o in ProfileBox.Items)
        {
            if (o is ComboBoxItem it && (it.Tag as string) == active)
            {
                ProfileBox.SelectedItem = it;
                break;
            }
        }
        if (ProfileBox.SelectedItem == null && ProfileBox.Items.Count > 0)
            ProfileBox.SelectedIndex = 0;
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中集合 → 目录列表填充该集合（切换集合，确定后重新加载）
        if (ProfileBox.SelectedItem is not ComboBoxItem item || item.Tag is not string name)
            return;
        if (!_services.Preferences.RootsProfiles.TryGetValue(name, out var dirs))
            return;
        RootsList.Items.Clear();
        foreach (var d in dirs)
            RootsList.Items.Add(d);
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var win = new Window
        {
            Title = _services.Localisation.Get("roots.profile_save"),
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var box = new TextBox { Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(new TextBlock { Text = _services.Localisation.Get("roots.profile_name") });
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
        var dirs = RootsList.Items.Cast<object>().Select(x => x.ToString()!).ToList();
        _services.Preferences.RootsProfiles[name] = dirs;
        _services.Preferences.ActiveRootsProfile = name;
        _services.Preferences.Save();
        RefreshProfileBox();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LangBox.SelectedItem is not ComboBoxItem item || item.Tag is not string lang)
            return;
        var loc = _services.Localisation;
        if (lang == loc.CurrentLanguage)
            return;
        try
        {
            loc.SetLanguage(lang);
            _services.Preferences.Language = lang;
            _services.Preferences.Save();
            // 刷新本窗口文本
            Title = loc.Get("roots.title");
            HintText.Text = loc.Get("roots.hint");
            LangLabel.Text = loc.Get("roots.language");
            OkButton.Content = loc.Get("roots.ok");
            CancelButton.Content = loc.Get("roots.cancel");
        }
        catch
        {
            // 防御性
        }
    }

    private void OnListRightUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        MenuItem Mi(string key, Action act)
        {
            var it = new MenuItem { Header = _services.Localisation.Get(key) };
            it.Click += (_, _) => act();
            return it;
        }
        menu.Items.Add(Mi("roots.add", () => OnAdd(this, new RoutedEventArgs())));
        menu.Items.Add(Mi("roots.remove", () => OnRemove(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi("roots.up", () => OnUp(this, new RoutedEventArgs())));
        menu.Items.Add(Mi("roots.down", () => OnDown(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi("roots.profile_save", () => OnSaveProfile(this, new RoutedEventArgs())));
        RootsList.ContextMenu = menu;
        RootsList.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = _services.Localisation.Get("roots.add_dialog"), Multiselect = true };
        if (dlg.ShowDialog() == true)
        {
            foreach (var folder in dlg.FolderNames)
            {
                if (!RootsList.Items.Contains(folder))
                    RootsList.Items.Add(folder);
            }
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        var items = RootsList.SelectedItems.Cast<object>().ToList();
        foreach (var item in items)
            RootsList.Items.Remove(item);
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        int i = RootsList.SelectedIndex;
        if (i > 0)
        {
            var item = RootsList.Items[i];
            RootsList.Items.RemoveAt(i);
            RootsList.Items.Insert(i - 1, item);
            RootsList.SelectedIndex = i - 1;
        }
    }

    private void OnDown(object sender, RoutedEventArgs e)
    {
        int i = RootsList.SelectedIndex;
        if (i >= 0 && i < RootsList.Items.Count - 1)
        {
            var item = RootsList.Items[i];
            RootsList.Items.RemoveAt(i);
            RootsList.Items.Insert(i + 1, item);
            RootsList.SelectedIndex = i + 1;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // 先触发加载浮窗（App 注入的回调——在窗口关闭前显示），再关闭本窗口
        _onConfirmed?.Invoke();
        _services.Preferences.Roots.Clear();
        foreach (var item in RootsList.Items)
            _services.Preferences.Roots.Add(item.ToString()!);
        // 同步激活集合：无集合（首次）→ 建 Default；有激活集合 → 更新
        if (string.IsNullOrEmpty(_services.Preferences.ActiveRootsProfile)
            || !_services.Preferences.RootsProfiles.ContainsKey(_services.Preferences.ActiveRootsProfile))
        {
            _services.Preferences.ActiveRootsProfile = "Default";
        }
        _services.Preferences.RootsProfiles[_services.Preferences.ActiveRootsProfile] = new List<string>(_services.Preferences.Roots);
        _services.Preferences.Save();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
