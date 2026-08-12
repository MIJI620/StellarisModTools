// 文件: Stellaris.Editor/Services/EngineServices.cs
// 应用级服务容器：持有适配器、三个引擎、界面本地化与偏好设置，
// 供各页面共享（初稿阶段的轻量依赖注入）。

using System.Collections.Generic;
using Stellaris.Engine.GalaxyMap;
using Stellaris.Engine.GalaxyStyle;
using Stellaris.Engine.ImageAsset;
using Stellaris.Engine.SpriteManagement;
using Stellaris.Parser;

namespace Stellaris.Editor;

/// <summary>
/// 引擎服务容器：初始化完成后挂载全部共享实例，页面经此访问。
/// </summary>
public sealed class EngineServices
{
    /// <summary>模组前缀（导出路径/精灵命名使用，规范 14.5）。</summary>
    public string ModPrefix { get; set; } = "smt";

    public UILocalisationManager Localisation { get; } = new();
    public UserPreferences Preferences { get; set; } = new();

    /// <summary>模组偏好（存在模组根目录 .smt/，含模组前缀与样式导出开关）。</summary>
    public ModPreferences? ModPrefs { get; set; }

    /// <summary>本地配置管理器（银河类别 galaxy.json 的存取，银河样式相关设置一律存此类别）。</summary>
    public Stellaris.Engine.LocalConfigManager.LocalConfigManager? ConfigManager { get; set; }

    public StellarisAdapter? Adapter { get; set; }
    public ImageAssetEngine? ImageEngine { get; set; }
    public SpriteManagementEngine? SpriteEngine { get; set; }
    public GalaxyStyleEngine? StyleEngine { get; set; }
    public GalaxyMapEngine? MapEngine { get; set; }
    public Stellaris.Engine.SystemInitializer.SystemInitializerEngine? SystemInitializerEngine { get; set; }
    public Stellaris.Engine.Localisation.LocalisationDictionaryEngine? DictionaryEngine { get; set; }
    public Stellaris.Engine.StaticModifier.StaticModifierEngine? StaticModifierEngine { get; set; }
    public Stellaris.Engine.EdictDecision.EdictDecisionEngine? EdictDecisionEngine { get; set; }
    public Stellaris.Engine.StrategicResource.StrategicResourceEngine? StrategicResourceEngine { get; set; }
    public Stellaris.Engine.Technology.TechnologyEngine? TechnologyEngine { get; set; }
    public Stellaris.Engine.Ship.ShipEngine? ShipEngine { get; set; }

    /// <summary>当前已配置的根目录列表（顺序 = 优先级）。</summary>
    public List<string> Roots => Preferences.Roots;

    public bool IsInitialized => Adapter != null && StyleEngine != null && MapEngine != null;
}
