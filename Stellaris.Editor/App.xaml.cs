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

    /// <summary>当前界面本地化管理器（静态——供无参构造的 XAML 转换器延迟获取）。</summary>
    public static UILocalisationManager? CurrentLocalisation { get; set; }

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
        CurrentLocalisation = services.Localisation;

        // 阶段 0：界面本地化载入
        services.Localisation.Load();

        // 阶段 1：偏好设置读取（偏好语言不可用时保持默认，防御性）
        services.Preferences = UserPreferences.Load();
        // 界面统一字号：所有输入框按用户设置（缺省 12——代码创建隐式 TextBox 样式）
        ApplyFontStyle((double)services.Preferences.FontSize);

        // 激活集合 → Roots（**先同步**——PrepareModConfig/迁移/扫描都要用正确的 Roots 计算 modRoot）
        SyncRootsFromActiveProfile(services.Preferences);

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
        bool hasAnything = services.Preferences.RootsProfiles.Count > 0;
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
            // 用户选了目录 → 重新准备模组配置（modRoot 更新为新的最后一位）
            PrepareModConfig(services);
        }
        // 激活集合有效 → Roots = 集合目录（用户记录的是用哪个播放集；启动/重载按集合加载）
        SyncRootsFromActiveProfile(services.Preferences);
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
            LoggerSetup.GetFactory().CreateLogger("Boot").LogInformation("扫描完成，开始构造主窗口");
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
            // 时序：先开主界面，再关浮窗（浮窗遮盖到主窗口就绪，视觉连续）
            var bootLog = LoggerSetup.GetFactory().CreateLogger("Boot");
            bootLog.LogInformation("主窗口构造完成，准备 Show");
            mainWindow.Show();
            bootLog.LogInformation("主窗口已 Show");
            overlay.Close();
            bootLog.LogInformation("浮窗已关闭，OnStartup 完成");
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

        // 本地配置管理器（银河类别 galaxy.json）：银河样式相关设置一律存此类别。
        // modRoot 为空（Roots 全空）时落到标准临时位置 sandbox/.smt（与 EnsureRootsFallback 的
        // explode 兜底目录一致——绝不把 galaxy.json 写到 exe 旁 config，不留垃圾）
        string configRoot = string.IsNullOrEmpty(modRoot)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, "sandbox", ".smt")
            : System.IO.Path.Combine(modRoot, ".smt");
        services.ConfigManager = new Stellaris.Engine.LocalConfigManager.LocalConfigManager(configRoot);
    }

    /// <summary>全局统一字号：创建/重建隐式 TextBox 样式（FontSize = size）。
    /// 用代码创建（避免 XAML sys:Double 在 net10 运行时解析崩溃）；设置页改字号时重建即时生效。</summary>
    public static void ApplyFontStyle(double size)
    {
        try
        {
            var style = new System.Windows.Style(typeof(System.Windows.Controls.TextBox));
            style.Setters.Add(new System.Windows.Setter(
                System.Windows.Controls.Control.FontSizeProperty, size));
            Application.Current.Resources[typeof(System.Windows.Controls.TextBox)] = style;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] 应用全局字号失败: {ex.Message}");
        }
    }

    /// <summary>激活集合（ActiveRootsProfile）有效 → 把 Roots 覆盖为集合目录。
    /// 用户已确认"记录的是用哪个播放集"——启动/重载一律按播放集加载，Roots 仅作内存实际目录。
    /// 集合为空或不存在时不动 Roots。返回是否已同步。</summary>
    private static bool SyncRootsFromActiveProfile(UserPreferences prefs)
    {
        // 激活集合缺失 → 默认第 1 个（用户要求：缺失默认播放第 1 个）
        if (string.IsNullOrEmpty(prefs.ActiveRootsProfile)
            || !prefs.RootsProfiles.ContainsKey(prefs.ActiveRootsProfile))
        {
            if (prefs.RootsProfiles.Count == 0)
                return false;
            prefs.ActiveRootsProfile = prefs.RootsProfiles.Keys.First();
        }
        var dirs = prefs.RootsProfiles[prefs.ActiveRootsProfile];
        prefs.Roots.Clear();
        prefs.Roots.AddRange(dirs);
        prefs.Save();
        return true;
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

        // 第一步：按激活集合同步 Roots（用户记录的是用哪个播放集）→ Roots 空则 explode 兜底
        SyncRootsFromActiveProfile(_services.Preferences);
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
                // 时序：先开主界面，再关浮窗（与启动一致）
                mainWindow.Show();
                overlay.Close();
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
    /// 后台线程初始化：分层绝对次序（用户规范）——
    /// 行1 SA → 行2 本地化+IA → 行3 子图形（单开）→ 行4 星系样式+加成+恒星系预设+战略资源 →
    /// 行5 地图+法令决议+科技。不同层绝对串行，行内不得跳跃/回调。
    /// 通过各引擎 TaskChanged 事件将进度汇报到浮层（规范 4.4）。
    /// </summary>
    private void InitializeEngines(EngineServices services, StatusOverlay overlay)
    {
        var loc = services.Localisation;
        void Report(string main, string? sub = null)
            => overlay.SetStatus(main, sub);

        // ---- SA 初始化（订阅扫描进度）----
        services.Adapter = new StellarisAdapter
        {
            // 解析层覆盖规则：只读一次（static_modifiers/events 等）→ 最早 root 生效
            Rules = new Stellaris.Parser.Rules.RulesReader(),
            // 标记为"游戏"的 root（只读一次跳过它——不算最早）
            GameRoot = services.Preferences.GameRoot
        };
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

        // ==================================================================
        // 引擎初始化绝对次序（用户规范）：不同层绝对串行，行内不得跳跃/回调。
        //   行1 SA 全局初始化 → 行2 本地化+IA → 行3 子图形（单开）
        //   → 行4 星系样式+加成 → 行5 地图+法令决议+科技（+战略资源+恒星系预设）
        // 每层完成才允许开启下一层；同一层内的引擎只读 Adapter、互不调用。
        // ==================================================================

        // ---- 行2：本地化 + 图像素材引擎（只依赖 SA）----
        // 语言字典引擎（只读；唯一允许使用正则的位置）
        services.DictionaryEngine = new Stellaris.Engine.Localisation.LocalisationDictionaryEngine(
            services.Adapter, LoggerSetup.GetFactory().CreateLogger("LocalisationDictionary"));
        services.ImageEngine = new ImageAssetEngine(services.Preferences.Roots);

        // ---- 行3：子图形引擎（单开一个阶段；依赖行2 的 IA）----
        Report(loc.Get("status.sprite_index"));
        services.SpriteEngine = new SpriteManagementEngine(services.Adapter, services.ImageEngine,
            LoggerSetup.GetFactory().CreateLogger("Sprite"));

        // ---- 行4：星系样式引擎 + 加成字典引擎 + 恒星系预设 + 战略资源
        //          （依赖行3 的子图形 / SA；恒星系预设与战略资源被行5 的地图/法令决议/科技调用，
        //           必须在行5 之前完成）----
        Report(loc.Get("status.loading_styles"));
        services.StyleEngine = new GalaxyStyleEngine(
            services.Adapter, services.ImageEngine, services.SpriteEngine, services.ModPrefix,
            configManager: services.ConfigManager,
            logger: LoggerSetup.GetFactory().CreateLogger("Style"));
        // 启用语种来自模组偏好（ModPreferences，与 ModPrefix 同级）
        services.StyleEngine.SetEnabledLanguages(services.ModPrefs?.EnabledLanguages);
        // 应用 galaxy.json 里保存的样式顺序（拖拽排序——重启/重载后恢复）
        services.StyleEngine.ApplyStoredStyleOrder();
        services.StyleEngine.TaskChanged += (_, args) =>
            Report(loc.Get("status.loading_styles"), args.Argument);

        // 加成字典引擎（只读；全 AST 扫描）——本层依赖 SA（行1 已全局完成），同步扫描
        var modEngine = new Stellaris.Engine.StaticModifier.StaticModifierEngine(
            services.Adapter, LoggerSetup.GetFactory().CreateLogger("StaticModifier"));
        services.StaticModifierEngine = modEngine;
        try
        {
            modEngine.ScanAll();
        }
        catch (Exception ex)
        {
            LoggerSetup.GetFactory().CreateLogger("StaticModifier").LogError(ex, "加成字典扫描失败");
        }

        // 恒星系预设引擎（行5 的地图/页面会调用——行4 内完成）
        services.SystemInitializerEngine = new SystemInitializerEngine(services.Adapter,
            LoggerSetup.GetFactory().CreateLogger("Initializer"));

        // 战略资源引擎：初始化时对固定路径做撞击重扫描（顶层 key 合并超大表）。
        // 法令/决议的 resources 模型复用本引擎（行5）——行4 内完成
        var resEngine = new Stellaris.Engine.StrategicResource.StrategicResourceEngine(
            services.Adapter, LoggerSetup.GetFactory().CreateLogger("StrategicResource"));
        resEngine.ScanAll();
        services.StrategicResourceEngine = resEngine;

        // ---- 行5：地图引擎 + 法令/决议 + 科技引擎（依赖前 4 行；科技依赖行4 的加成引擎）----
        Report(loc.Get("status.loading_maps"));
        services.MapEngine = new GalaxyMapEngine(
            services.Adapter, services.StyleEngine, services.ImageEngine,
            services.SpriteEngine, services.ModPrefix,
            LoggerSetup.GetFactory().CreateLogger("Map"),
            configManager: services.ConfigManager);
        services.MapEngine.TaskChanged += (_, args) =>
            Report(loc.Get("status.loading_maps"), args.Argument);
        services.MapEngine.ScanAll();

        // 法令/决议引擎（只读扫描 + 内存新建 + 字段级保存）
        services.EdictDecisionEngine = new Stellaris.Engine.EdictDecision.EdictDecisionEngine(
            services.Adapter, LoggerSetup.GetFactory().CreateLogger("EdictDecision"),
            services.ModPrefs?.EnabledLanguages);

        // 科技引擎（只读浏览 + 专属索引；modifier 本地化复用 StaticModifierEngine）
        // 依赖行4 的加成引擎（已同步扫描完成）——前置引擎完成后才开启自身
        var techEngine = new Stellaris.Engine.Technology.TechnologyEngine(
            services.Adapter, modEngine, LoggerSetup.GetFactory().CreateLogger("Technology"));
        services.TechnologyEngine = techEngine;
        try
        {
            techEngine.ScanAll();
        }
        catch (Exception ex)
        {
            LoggerSetup.GetFactory().CreateLogger("Technology").LogError(ex, "科技引擎索引失败");
        }

        // 舰船引擎（只读索引）：舰船文件夹根 block 的解锁索引 + 本地化名（命名规则特殊）
        var shipEngine = new Stellaris.Engine.Ship.ShipEngine(
            services.Adapter, LoggerSetup.GetFactory().CreateLogger("Ship"));
        services.ShipEngine = shipEngine;
        try
        {
            shipEngine.ScanAll();
        }
        catch (Exception ex)
        {
            LoggerSetup.GetFactory().CreateLogger("Ship").LogError(ex, "舰船引擎索引失败");
        }

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

        // ---- 通用初始化段：半隐藏自用拓展（key 提取导出）----
        // 非功能——不建引擎；若 .smt/_key_extract.json 存在则扫描全部配置并按 100% 匹配 key
        // 提取 block/list 值，导出 .smt/_key_extract.md（用户正常接触不到该配置）
        try
        {
            RunKeyExtractIfConfigured(services);
        }
        catch (Exception ex)
        {
            LoggerSetup.GetFactory().CreateLogger("KeyExtract").LogError(ex, "key 提取导出失败");
        }

        Report(loc.Get("status.done"));
    }

    /// <summary>
    /// 半隐藏自用拓展（非功能——不建引擎）：读取 Roots 最后一位的 .smt/_key_extract.json
    /// （不存在 → 跳过）。配置 = {"keys": ["..."]} 或直接数组。对全部配置文件（SA 合并 AST）
    /// 做 **key 100% 匹配**（Ordinal）搜索：匹配节点为 Block → 收集顶级 Simple 子键；
    /// 为 List → 收集子值去重合并。结果导出为 .smt/_key_extract.md（经 SA WriteExportFile）。
    /// </summary>
    private void RunKeyExtractIfConfigured(EngineServices services)
    {
        var adapter = services.Adapter;
        if (adapter == null)
            return;
        const string configRel = ".smt/_key_extract.json";
        const string exportRel = ".smt/_key_extract.md";
        var raw = adapter.ReadTextFile(configRel);
        if (string.IsNullOrWhiteSpace(raw))
            return;   // 半隐藏：未配置 → 完全跳过
        var keys = new List<string>();
        using (var doc = System.Text.Json.JsonDocument.Parse(raw))
        {
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                        keys.Add(el.GetString() ?? "");
            }
            else if (doc.RootElement.TryGetProperty("keys", out var arr)
                     && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == System.Text.Json.JsonValueKind.String)
                        keys.Add(el.GetString() ?? "");
            }
        }
        if (keys.Count == 0)
            return;
        // 扫描全部配置文件（合并 AST）
        var results = new Dictionary<string, (List<string> Blocks, List<string> Lists)>(StringComparer.Ordinal);
        foreach (var key in keys)
            results[key] = (new List<string>(), new List<string>());
        foreach (var result in adapter.GetAllConfigs().Values)
        {
            foreach (var root in result.RootNodes)
                WalkKeyExtract(root, results);
        }
        // 生成 md：每个 key **单独一节**——`## key` + `> key` 引用块，值**逐行**列出（不逗号合并），
        // key 之间多空几行（换行不要钱）
        var sb = new System.Text.StringBuilder();
        foreach (var key in keys)
        {
            var (blocks, lists) = results[key];
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## " + key);
            sb.AppendLine("> " + key);
            foreach (var b in blocks)
                sb.AppendLine(b);
            foreach (var l in lists)
                sb.AppendLine(l);
        }
        adapter.WriteExportFile(exportRel, sb.ToString());
        LoggerSetup.GetFactory().CreateLogger("KeyExtract")
            .LogInformation("key 提取导出完成: {Count} 个 key → {Rel}", keys.Count, exportRel);
    }

    /// <summary>递归遍历：节点 Key 100% 匹配 → Block 收集顶级 Simple 子键；List 收集子值去重。</summary>
    private static void WalkKeyExtract(Stellaris.Parser.AstNode node,
        Dictionary<string, (List<string> Blocks, List<string> Lists)> results)
    {
        if (node == null)
            return;
        if (!string.IsNullOrEmpty(node.Key) && results.TryGetValue(node.Key, out var bucket))
        {
            if (node.Children != null && node.Children.Count > 0)
            {
                if (node.Type == Stellaris.Parser.NodeType.List)
                {
                    foreach (var child in node.Children)
                    {
                        var v = child.Value?.ToString();
                        if (!string.IsNullOrEmpty(v) && !bucket.Lists.Contains(v))
                            bucket.Lists.Add(v);
                    }
                }
                else
                {
                    foreach (var child in node.Children)
                    {
                        if (child.Type == Stellaris.Parser.NodeType.Simple && !string.IsNullOrEmpty(child.Key)
                            && !bucket.Blocks.Contains(child.Key))
                            bucket.Blocks.Add(child.Key);
                    }
                }
            }
        }
        if (node.Children != null)
            foreach (var child in node.Children)
                WalkKeyExtract(child, results);
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
