// 文件: Stellaris.Editor/App.xaml.cs
// 应用入口：载入界面本地化 → 读偏好 → （无 roots 则弹选择界面）→
// 显示状态浮层 → 后台线程初始化 SA 与三个引擎（任务事件实时汇报）→
// 完成进入主窗口 / 失败提示退出（规范总体执行顺序阶段 0~8、4.4）。

using System.Windows;
using Microsoft.Extensions.Logging;
using Stellaris.Engine.GalaxyMap;
using Stellaris.Engine.GalaxyStyle;
using Stellaris.Engine.ImageAsset;
using Stellaris.Engine.SpriteManagement;
using Stellaris.Engine.SystemInitializer;
using Stellaris.Parser;

namespace Stellaris.Editor;

public partial class App : Application
{
    /// <summary>应用级服务容器（OnStartup 初始化，重载入流程复用）。</summary>
    private EngineServices? _services;

    /// <summary>关闭窗口/退出程序时自动保存程序 config 文件夹的全部用户配置（当前为 user_prefs.json）；
    /// 若本次使用了 explode 兜底目录（UI 隐藏），关闭时弹窗告知并给出"打开文件夹"按钮。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            var prefs = _services?.Preferences;
            if (prefs != null)
            {
                prefs.Save();
                string explode = System.IO.Path.Combine(AppContext.BaseDirectory, "sandbox");
                // 自选目录为空（Roots 空 或 只有 sandbox 兜底）→ 关闭时必弹告知（无论是否保存过）
                bool usedFallback = prefs.Roots.Count == 0
                    || (prefs.Roots.Count == 1 && !string.IsNullOrEmpty(prefs.Roots[0])
                        && string.Equals(System.IO.Path.GetFullPath(prefs.Roots[0]),
                            System.IO.Path.GetFullPath(explode), StringComparison.OrdinalIgnoreCase));
                if (usedFallback)
                    ShowExplodeNotice(explode);
            }
        }
        catch { }
        base.OnExit(e);
    }

    private void ShowExplodeNotice(string explodePath)
    {
        var loc = _services?.Localisation;
        var win = new Window
        {
            Title = loc?.Get("explode.title") ?? "Explode",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = true
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = loc?.Get("explode.notice") ?? "Data was saved to the hidden fallback folder:",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new System.Windows.Controls.TextBox
        {
            Text = explodePath,
            IsReadOnly = true,
            Margin = new Thickness(0, 0, 0, 12)
        });
        var openBtn = new System.Windows.Controls.Button
        {
            Content = loc?.Get("explode.open") ?? "Open Folder",
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        openBtn.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", explodePath); } catch { }
        };
        var okBtn = new System.Windows.Controls.Button
        {
            Content = loc?.Get("common.ok") ?? "OK",
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        okBtn.Click += (_, _) => win.DialogResult = true;
        var btnRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        btnRow.Children.Add(openBtn);
        btnRow.Children.Add(okBtn);
        panel.Children.Add(btnRow);
        win.Content = panel;
        win.ShowDialog();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 初始化日志（清空 editor_debug.log；引擎 logger 使用同一工厂，避免 NullLogger 静默）
        LoggerSetup.Initialize();

        // 全局兜底：任何未处理的 UI 线程异常写日志并提示，绝不静默崩溃
        DispatcherUnhandledException += (_, args) =>
        {
            WriteErrorLog(args.Exception);
            MessageBox.Show(
                $"Unhandled UI exception:\n{args.Exception.Message}\n\nDetails written to error.log",
                "Stellaris Mod Tools", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var services = new EngineServices();
        _services = services;

        // 阶段 0：界面本地化载入
        services.Localisation.Load();

        // 阶段 1：偏好设置读取（偏好语言不可用时保持默认，防御性）
        services.Preferences = UserPreferences.Load();

        // 模组偏好：存于最高优先级根目录的 .smt/（模组前缀、样式导出开关）
        PrepareModConfig(services);

        // 历史遗留迁移：曾存于 ModPreferences（用户配置类别）的样式独立开关
        // （StyleFlags）→ 归位到银河类别 galaxy.json（保存流程读取的类别），
        // 迁移后从 ModPreferences 清除，避免"放错类别不生效"。
        if (services.ModPrefs?.StyleFlags is { Count: > 0 } legacyFlags
            && services.ConfigManager != null)
        {
            var batch = new Dictionary<string, object>();
            foreach (var kv in legacyFlags)
            {
                batch[$"styles.{kv.Key}.preview"] = kv.Value.Preview;
                batch[$"styles.{kv.Key}.icon"] = kv.Value.Icon;
            }
            try
            {
                services.ConfigManager.SetBatch("galaxy", batch);
                services.ModPrefs.StyleFlags = null;
                string modRoot = services.Preferences.Roots.Count > 0
                    ? services.Preferences.Roots[^1]
                    : string.Empty;
                services.ModPrefs.Save(modRoot);
                System.Diagnostics.Debug.WriteLine("[App] 已迁移历史 StyleFlags 到 galaxy.json");
            }
            catch
            {
                // 迁移失败不阻塞启动
            }
        }

        try
        {
            services.Localisation.SetLanguage(services.Preferences.Language);
        }
        catch
        {
            // 忽略：保持默认语言
        }

        // 阶段 2：无 roots → 弹出 Roots 多选界面；取消则退出。
        // 时序：确认时**先显示加载浮窗，再关闭选择窗口**（用户确认后立刻看到加载，而非窗口先消失）。
        var overlay = new StatusOverlay();
        overlay.Title = services.Localisation.Get("app.title");
        // 有集合（RootsProfiles）就不弹选择界面——默认打开上一次使用的集合（ActiveRootsProfile）
        bool hasAnything = services.Preferences.HasRoots || services.Preferences.RootsProfiles.Count > 0;
        if (!hasAnything)
        {
            var rootsWindow = new RootsWindow(services);
            rootsWindow.SetOnConfirmed(() =>
            {
                overlay.Show();
                overlay.SetMain(services.Localisation.Get("status.initializing"));
            });
            if (rootsWindow.ShowDialog() != true || services.Preferences.Roots.Count == 0)
            {
                Shutdown();
                return;
            }
        }
        // Roots 为空：激活集合有目录 → 用上次集合；否则 exe 旁 explode 兜底（UI 隐藏，忘设置也能保存）
        EnsureRootsFallback(services.Preferences);

        // 阶段 3 ~ 7：状态浮层 + 后台初始化（UI 不阻塞，实时汇报进度）
        if (!overlay.IsVisible)
        {
            overlay.Show();
            overlay.SetMain(services.Localisation.Get("status.initializing"));
        }

        try
        {
            await System.Threading.Tasks.Task.Run(() => InitializeEngines(services, overlay));
        }
        catch (Exception ex)
        {
            // 失败：浮层显示详细错误（红字 + 退出按钮），停留供用户查看，
            // 不静默退出；同时写错误日志到 exe 旁 error.log 便于反馈排查。
            string msg = services.Localisation.Format("error.init_failed", ex.Message);
            WriteErrorLog(ex);
            overlay.ShowError(msg);
            return;
        }

        // 阶段 8：进入主界面（先构造主窗口，成功后再关闭浮层；
        // 失败时浮层仍在，可显示错误红字 + 退出按钮，而非崩溃）
        try
        {
            var mainWindow = new MainWindow(services);
            MainWindow = mainWindow;
            overlay.Close();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            WriteErrorLog(ex);
            string msg = services.Localisation.Format("error.init_failed", ex.Message);
            try
            {
                if (overlay.IsVisible)
                    overlay.ShowError(msg);
                else
                    MessageBox.Show(msg, "Stellaris Mod Tools", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 兜底：错误呈现本身失败也不得再抛
            }
        }
    }

    /// <summary>
    /// 按当前根目录准备模组级配置：模组偏好（.smt/）、前缀、本地配置管理器。
    /// 根目录列表变化（启动 / 重载入）后调用，保证三者与新 roots 一致。
    /// </summary>
    private void PrepareModConfig(EngineServices services)
    {
        string modRoot = services.Preferences.Roots.Count > 0
            ? services.Preferences.Roots[^1]
            : string.Empty;
        services.ModPrefs = ModPreferences.Load(modRoot);
        services.ModPrefix = services.ModPrefs.ModPrefix;

        // 本地配置管理器（银河类别 galaxy.json）：银河样式相关设置一律存此类别
        string configRoot = string.IsNullOrEmpty(modRoot)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "config")
            : System.IO.Path.Combine(modRoot, ".smt");
        services.ConfigManager = new Stellaris.Engine.LocalConfigManager.LocalConfigManager(configRoot);
    }

    /// <summary>
    /// Roots 空时的兜底：激活集合有目录 → 用上次集合；否则用 exe 旁 explode（UI 隐藏的兜底目录，
    /// 用户忘设置也能保存）。返回是否注入了 explode。
    /// </summary>
    private static bool EnsureRootsFallback(UserPreferences prefs)
    {
        if (prefs.Roots.Count > 0)
            return false;
        bool filled = false;
        if (!string.IsNullOrEmpty(prefs.ActiveRootsProfile)
            && prefs.RootsProfiles.TryGetValue(prefs.ActiveRootsProfile, out var lastDirs)
            && lastDirs.Count > 0)
        {
            prefs.Roots.AddRange(lastDirs);
            filled = true;
        }
        if (!filled)
        {
            string explode = System.IO.Path.Combine(AppContext.BaseDirectory, "sandbox");
            System.IO.Directory.CreateDirectory(explode);
            prefs.Roots.Add(explode);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 重载入（设置页触发）：路径列表已保存到本地配置 → 关闭主画面窗口 →
    /// 按现有目录集从头初始化引擎并重建主窗口（不再弹出目录选择窗口）。
    /// </summary>
    public async void RestartFromRoots()
    {
        if (_services == null)
            return;

        // 第一步：Roots 空（如空集合）→ explode 兜底；确保最新路径列表已写入本地用户配置
        EnsureRootsFallback(_services.Preferences);
        _services.Preferences.Save();

        // 时序保护：关闭主窗口是"最后一个窗口"，默认 OnLastWindowClose 会在
        // Close 的瞬间触发 Application.Shutdown，导致后续初始化浮层无法显示。
        // 重载全程临时切到显式关机，等新主窗口就绪后再恢复。
        var prevShutdownMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            // 第二步：关闭主画面窗口
            if (MainWindow is MainWindow main)
                main.Close();

            // 模组级配置依赖最高优先级根目录，按当前 roots 重新准备
            PrepareModConfig(_services);

            // 第三步：从头进行文件扫描（状态浮层 + 后台初始化，UI 不阻塞）
            var overlay = new StatusOverlay();
            overlay.Title = _services.Localisation.Get("app.title");
            overlay.Show();
            overlay.SetMain(_services.Localisation.Get("status.initializing"));

            try
            {
                await System.Threading.Tasks.Task.Run(() => InitializeEngines(_services, overlay));
            }
            catch (Exception ex)
            {
                string msg = _services.Localisation.Format("error.init_failed", ex.Message);
                WriteErrorLog(ex);
                overlay.ShowError(msg);
                return;
            }

            try
            {
                var mainWindow = new MainWindow(_services);
                MainWindow = mainWindow;
                overlay.Close();
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                WriteErrorLog(ex);
                string msg = _services.Localisation.Format("error.init_failed", ex.Message);
                try
                {
                    if (overlay.IsVisible)
                        overlay.ShowError(msg);
                    else
                        MessageBox.Show(msg, "Stellaris Mod Tools", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch
                {
                    // 兜底：错误呈现本身失败也不得再抛
                }
            }
        }
        finally
        {
            // 恢复默认关机模式（窗口全关才退出）
            ShutdownMode = prevShutdownMode;
        }
    }

    /// <summary>
    /// 后台线程初始化：SA → IA → SM → GalaxyStyle → GalaxyMap。
    /// 通过各引擎 TaskChanged 事件将进度汇报到浮层（规范 4.4）。
    /// </summary>
    private void InitializeEngines(EngineServices services, StatusOverlay overlay)
    {
        var loc = services.Localisation;
        void Report(string main, string? sub = null)
            => overlay.SetStatus(main, sub);

        // ---- SA 初始化（订阅扫描进度）----
        services.Adapter = new StellarisAdapter();
        services.Adapter.TaskChanged += (_, args) =>
        {
            string? text = args.TaskType switch
            {
                ParserTaskType.ParsingFile => loc.Format("status.scanning", args.Argument ?? string.Empty),
                ParserTaskType.LoadingGlobals => loc.Get("status.loading_globals"),
                ParserTaskType.ConstantEvaluation => loc.Get("status.evaluating"),
                ParserTaskType.InlineScriptExpand => loc.Get("status.expanding"),
                ParserTaskType.CsvMerge => loc.Get("status.csv_merge"),
                ParserTaskType.LocalizationFormat => loc.Get("status.localisation"),
                _ => null
            };
            if (text != null)
                Report(text);
        };

        foreach (var root in services.Preferences.Roots)
            services.Adapter.AddRoot(root);
        Report(loc.Get("status.scanning_all"));
        services.Adapter.ScanAll();

        // ---- ImageAssetEngine / SpriteManagementEngine ----
        services.ImageEngine = new ImageAssetEngine(services.Preferences.Roots);
        Report(loc.Get("status.sprite_index"));
        services.SpriteEngine = new SpriteManagementEngine(services.Adapter, services.ImageEngine,
            LoggerSetup.GetFactory().CreateLogger("Sprite"));

        // ---- GalaxyStyleEngine ----
        Report(loc.Get("status.loading_styles"));
        services.StyleEngine = new GalaxyStyleEngine(
            services.Adapter, services.ImageEngine, services.SpriteEngine, services.ModPrefix,
            configManager: services.ConfigManager,
            logger: LoggerSetup.GetFactory().CreateLogger("Style"));
        // 启用语种来自模组偏好（ModPreferences，与 ModPrefix 同级）
        services.StyleEngine.SetEnabledLanguages(services.ModPrefs?.EnabledLanguages);
        services.StyleEngine.TaskChanged += (_, args) =>
            Report(loc.Get("status.loading_styles"), args.Argument);

        // ---- GalaxyMapEngine ----
        Report(loc.Get("status.loading_maps"));
        services.MapEngine = new GalaxyMapEngine(
            services.Adapter, services.StyleEngine, services.ImageEngine,
            services.SpriteEngine, services.ModPrefix,
            LoggerSetup.GetFactory().CreateLogger("Map"),
            configManager: services.ConfigManager);
        services.MapEngine.TaskChanged += (_, args) =>
            Report(loc.Get("status.loading_maps"), args.Argument);
        services.MapEngine.ScanAll();

        // 恒星系预设引擎（第一阶段：扫描；后续可视化编辑）
        services.SystemInitializerEngine = new SystemInitializerEngine(services.Adapter,
            LoggerSetup.GetFactory().CreateLogger("Initializer"));

        // 语言字典引擎（只读；唯一允许使用正则的位置）
        services.DictionaryEngine = new Stellaris.Engine.Localisation.LocalisationDictionaryEngine(
            services.Adapter, LoggerSetup.GetFactory().CreateLogger("LocalisationDictionary"));

        // 默认锁定本地化：原版预设（huge/large/medium/small/tiny 等，galaxy.json 写死可改）
        try
        {
            var cm = services.ConfigManager;
            var defaultLock = new List<string> { "huge", "large", "medium", "small", "tiny" };
            if (cm != null)
            {
                var lv = cm.Get("galaxy", "default_lock_localisation");
                if (lv is System.Text.Json.Nodes.JsonArray arr && arr.Count > 0)
                {
                    defaultLock.Clear();
                    foreach (var n in arr)
                    {
                        if (n is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                            defaultLock.Add(s);
                    }
                }
                else
                {
                    // 首次：写入默认列表（写死原版预设名，用户可改）
                    cm.SetBatch("galaxy", new Dictionary<string, object>
                    {
                        ["default_lock_localisation"] = defaultLock.ToArray()
                    });
                }
            }
            services.MapEngine.ApplyDefaultLockLocalisation(defaultLock);
        }
        catch
        {
            // 配置读取失败 → 忽略
        }

        // 恢复静态地图绑定样式 + 各地图的锁定/清空本地化（galaxy.json maps 节点）
        // core_radius 是样式参数，从样式文件（galaxy_shapes.txt）读取，不读配置
        try
        {
            var cm = services.ConfigManager;
            if (cm != null)
            {
                var mv = cm.Get("galaxy", "maps");
                if (mv is System.Text.Json.Nodes.JsonObject maps)
                {
                    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                    var flags = new Dictionary<string, (bool Lock, bool Clear)>(StringComparer.Ordinal);
                    foreach (var kv in maps)
                    {
                        if (kv.Value is System.Text.Json.Nodes.JsonObject entry)
                        {
                            var bs = entry["bound_style"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(bs))
                                dict[kv.Key] = bs;
                            bool lk = entry["lock_localisation"] is System.Text.Json.Nodes.JsonValue lv
                                      && lv.TryGetValue<bool>(out var lb) && lb;
                            bool cl = entry["clear_file"] is System.Text.Json.Nodes.JsonValue cv
                                      && cv.TryGetValue<bool>(out var cb) && cb;
                            if (lk || cl)
                                flags[kv.Key] = (lk, cl);
                        }
                    }
                    services.MapEngine.RestoreStaticStyleMapping(dict);
                    services.MapEngine.RestoreMapFlags(flags);
                    // 形状总表顺序（maps.{name}.shape_order）→ 恢复拖拽排序
                    var shapeOrders = new Dictionary<string, System.Collections.Generic.List<string>>(StringComparer.Ordinal);
                    foreach (var kv in maps)
                    {
                        if (kv.Value is System.Text.Json.Nodes.JsonObject e2
                            && e2["shape_order"] is System.Text.Json.Nodes.JsonArray arr)
                        {
                            var list = new System.Collections.Generic.List<string>();
                            foreach (var item in arr)
                            {
                                var s = item?.GetValue<string>();
                                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                            }
                            if (list.Count > 0) shapeOrders[kv.Key] = list;
                        }
                    }
                    services.MapEngine.RestoreShapeTableOrder(shapeOrders);
                }
            }
        }
        catch
        {
            // 无映射或格式不符 → 忽略（同名兜底已由 RebuildStaticStyleMapping 处理）
        }

        Report(loc.Get("status.done"));
    }

    /// <summary>将初始化异常完整写入 exe 旁 error.log（含内部异常与堆栈）。</summary>
    private static void WriteErrorLog(Exception ex)
    {
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "error.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 初始化失败");
            for (Exception? cur = ex; cur != null; cur = cur.InnerException)
            {
                sb.AppendLine($"  {cur.GetType().Name}: {cur.Message}");
                sb.AppendLine(cur.StackTrace);
            }
            System.IO.File.AppendAllText(path, sb.ToString());
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }
}
