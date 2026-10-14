// ============================================================================
//  「三倍加速」Mod —— 在游戏自带「加速」复选框的**正下方**新增一个勾选框。
//
//  行为（用户需求 2026-10-01）：
//    · 勾选本勾选框 ⇒ 在**原有加速的基础上再双倍**（= 原有加速速度 ×2）
//    · 同时**取消原有的加速**（两者互斥，绝不会叠加成两层）
//    · 再次点击本勾选框取消 ⇒ 回到普通速度
//    · 取消勾选原加速时，本勾选框也同步取消（不会残留"只勾了新框"的错觉）
//    · 位置：原「加速」**正下方**（两个加速框相邻）；「时停」由 TimeStop v1.0.31+ 自动排到本框之后
//
//  ── 游戏原有加速是怎么实现的（读源码得到的硬事实）────────────────────
//   `Scene/TowerDefesne/TowerDefenseControl.cs`：
//       public void CheckBox2XToggled(bool toggled)
//       {
//           if (toggled) {
//               AudioManager.Instance.AudioPlay("2XSpeedOn");
//               if (levelConfig is TowerDefenseLevelConfig { finishMethod: not QUIZ } c)
//                   Global.TimeScale = c.baseTimeScale * 1.5;
//               else if (levelConfig is TowerDefenseLevelConfig c2)
//                   Global.TimeScale = c2.baseTimeScale * 3.0;
//           } else {
//               AudioManager.Instance.AudioPlay("2XSpeedDown");
//               Global.TimeScale = (levelConfig is TowerDefenseLevelConfig c3) ? c3.baseTimeScale : 1.0;
//           }
//       }
//   而 `Core/Global/Global.cs`：
//       public double timeScale { set { _timeScale = value; Engine.TimeScale = _timeScale; … } }
//   ⇒ **加速控制的就是 `Global.TimeScale`（引擎级时间缩放）**，没有别的通道。
//     所以"双倍"就是把它乘 2。
//
//  ── 按钮位置为什么不会撞「时停」──────────────────────────────────────
//   「时停」(TimeStop) 把自己挂在**左下角齿轮 optionButton 的右侧**
//   （`anchorBtn.GlobalPosition.Y + anchorBtn.Size.Y + 6f`，见 TimeStopEntry.cs）。
//   本 Mod 挂的是**右上角「加速」的正下方**（`CheckBox2X`，GUITop 下，
//   anchor_left=1.0、offset_left=-107、offset_top=64）。两者相隔整个屏幕。
//
//  ── 踩坑（本项目已付出代价的）────────────────────────────────────────
//   1. Godot 4 的 `Callable` **没有** `Bind()`（那是 Godot 3 的写法）；
//      要带参数就用"无参 Action + 闭包"现造 Callable。
//   2. 不要用 `Convert.*(object)` —— 游戏附带的 .NET 运行时是**裁剪过**的，
//      `Convert.ToString(object)` 等会抛 `Method not found`。
//   3. 入口三回调（Initialize / OnAllModsLoaded / Shutdown）**绝不能抛**，
//      否则整包回滚；每帧逻辑一律 try/catch 并在外面吞掉。
//   4. 不要给入口类写自定义 `_Process` —— 本工程是**手写 csproj**，
//      没有 Godot 源生成器，`_Process` 永远不会被调用。
//      必须用 `SceneTree` 的 `process_frame` / `physics_frame` 信号。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

public sealed class DoubleSpeedToggleEntry : IXWModRuntimeEntry
{
	// 赞助码注入器（SponsorQrInjector.cs 共享组件）用的包根缓存
	internal static string SponsorQrRoot = null;

	private const string LogPrefix = "[DoubleSpeedToggle] ";
	private const string NewBoxName = "ModDoubleSpeedToggle";

	/// <summary>本勾选框相对「加速」速度的倍数。改这一个数即可调档位。</summary>
	private const double DoubleFactor = 2.0;

	/// <summary>
	/// 垂直堆叠间距（像素）。
	/// 取 **6** 是为了与「时停」保持一致 —— TimeStop 用的正是
	/// `anchorBtn.GlobalPosition.Y + anchorBtn.Size.Y + 6f`（见 TimeStopEntry.cs），
	/// 这样三个控件的间距看起来是同一套。
	/// </summary>
	private const float VGapPx = -4f;   // ★ v1.1.11：负值 —— 抵消两个复选框控件内部的空白（各自约 7px），让图标真正贴近

	/// <summary>
	/// 勾选框**初始**文本（v1.0.3）。
	/// ⚠️ 界面上显示的**真实倍率**由 `SyncBox()` 按**关卡实际 `baseTimeScale`** 算出后覆盖：
	///   普通关 → `3x`；`baseTimeScale = 2.0` 的 14 个关卡 → `6x`；QUIZ 关 → `6x`。
	///   这里只是"还没算出来之前"的占位（普通关的常见值），第 1 帧就会被覆盖。
	/// </summary>
	private const string BoxInitText = "3x";

	/// <summary>日志里用的中文名（**不显示在界面上** —— 界面显示实际倍率）。</summary>
	private const string BoxText = "三倍加速";

	/// <summary>
	/// 诊断日志总开关。排查时置 true 重新编译。
	/// ⚠️ `Diag()` 方法体内**必须**是 `GD.Print`，绝不能写成 `Diag` ——
	///   本项目踩过：脚本批量替换时把方法体自己也换了 ⇒ 无限递归 ⇒
	///   `StackOverflowException`（catch 抓不住，直接终止线程）。
	/// </summary>
	private static bool EnableDiagLog;   // 由 Initialize 从「Mod 配置」读取（原为编译期常量）

	// ================================================================ 按钮样式（内部开关）

	/// <summary>
	/// 加速按钮样式 —— **改这里 + 重新构建**即可切换：
	///   1 = 三格横排（`->` `->>` `->>>`）
	///   2 = 三格竖排（上下结构，矩形更高）
	///   3 = 单格：只显示当前档位，单击循环
	///   4 = 原有复选框（游戏「加速」+ 本 Mod 复选框两个）
	///
	/// 样式 1/2/3 会**隐藏游戏自带的「加速」复选框**并在它的位置放自建选择器 ——
	/// 因为三档里已经包含了"原版加速"这一档，两套并存会重复且互相打架。
	/// </summary>
	internal enum BtnStyle
	{
		TriHorizontal = 1,
		TriVertical = 2,
		SingleGear = 3,
		CheckBox = 4,
	}

	/// <summary>
	/// ★ 当前样式 —— 由「Mod 配置」面板决定（applyMode: restart ⇒ 重启游戏后生效）。
	/// 因为切换样式要重建整套控件与素材，用面板的 restart 模式最稳。
	/// </summary>
	private static BtnStyle Style = BtnStyle.TriHorizontal;
		private const string KPositionMode = "positionMode";
		private const string KCustomX = "customX";
		private const string KCustomY = "customY";
		private static bool _posCustom;
		private static int _posX = 10;
		private static int _posY = 200;
	private const string KStyle = "style";

	// ★ 点击方式（2026-10-09 用户定稿）：**不再提供开关**。
	//   样式 1/2 是三格各对应一档 ⇒ **点哪档就跳哪档**（直接选中）；
	//   样式 3 只有一格 ⇒ 只能**单击循环**。其余方式（整条循环 / 混合）已按用户要求删除。

	/// <summary>三档的箭头字形（用户给的三段式箭头图：单箭头 / 双箭头 / 三箭头）。</summary>
	private static readonly string[] GearArrow = { "->", "->>", "->>>" };

	/// <summary>档位文案（只在日志里用，不显示在界面上）。</summary>
	private static readonly string[] GearName = { "正常速度", "游戏加速", "Mod 加速" };

	// 配色（2026-10-09 用户定稿）：**颜色表示档位**
	//   档位 1（正常速度）= 绿 ；档位 2（游戏加速）= 橙 ；档位 3（Mod 加速）= 红
	private static readonly Color[] GearBg =
	{
		new Color(0.18f, 0.72f, 0.26f),   // 档位 1 · 绿
		new Color(1.00f, 0.52f, 0.06f),   // 档位 2 · 橙
		new Color(0.86f, 0.13f, 0.09f),   // 档位 3 · 红
	};
	// 未选中态：同色压暗（样式 1/2 三格并存时，靠明暗区分"当前选中"）
	private static readonly Color[] GearBgDim =
	{
		new Color(0.09f, 0.36f, 0.13f),
		new Color(0.50f, 0.26f, 0.03f),
		new Color(0.43f, 0.07f, 0.05f),
	};
	// 文字：白字 + 黑描边（在绿/橙/红上都清楚）
	private static readonly Color CellFg = new Color(1.00f, 1.00f, 1.00f);
	private static readonly Color CellFgOutline = new Color(0f, 0f, 0f, 1f);
	private static readonly Color FrameBg = new Color(0.05f, 0.03f, 0.02f);
	private static readonly Color FrameLine = new Color(0.02f, 0.01f, 0.01f);

	/// <summary>
	/// 是否使用手绘素材（`Resources/*.png`）。
	///   true  = 外框/格子/箭头全部用贴图（箭头是图形，不受字体影响）
	///   false = 退回**纯代码绘制**（StyleBoxFlat + 文字箭头 `<->`）——素材缺失时也会自动退回
	/// </summary>
	private static readonly bool UseArt = true;

	/// <summary>素材模式下单格的基准尺寸（1× 逻辑像素；贴图是 3 倍图，靠九宫拉伸）。</summary>
	private const float ArtCellW = 32f;
	private const float ArtCellH = 28f;
	/// <summary>九宫边距：贴图里不可拉伸的区域（= 3 倍图下的圆角 + 描边）。</summary>
	private const int ArtMargin = 30;

	/// <summary>箭头基础字号（装不下的格子会自动按比例缩小）。</summary>
	private const int CellFontSize = 16;
	/// <summary>格子样式里的左右内边距合计（见 EnsureCellStyles）。</summary>
	private const float CellMarginX = 4f;

	/// <summary>每格宽 / 高（像素，未缩放）。</summary>
	private const float CellW = 26f;   // ★ v1.1.5：只作下限；实际宽度由一二档文字决定
	private const float CellH = 28f;
	/// <summary>竖排（样式 2）时每格高一点 —— 用户要求矩形高度可以做高点。</summary>
	private const float CellHVertical = 34f;
	/// <summary>格间距 —— 透出深色底框，形成参考图里那种"分格线"。</summary>
	private const float CellGap = 3f;
	/// <summary>外框内边距。</summary>
	private const float FramePad = 2f;
	/// <summary>外框圆角（参考图是胶囊形，圆角给大些）。</summary>
	private const float FrameRadius = 10f;
	/// <summary>样式 1/2/3 的选择器根节点名（**必须以 Mod 开头** —— 「时停」靠这个前缀把它当成 Mod 控件、自动排到下方）。</summary>
	private const string SelName = "ModSpeedSelector";

	private XWModRuntimeContext _context;
	private SceneTree _tree;

	/// <summary>挂到 `ProcessFrame` 的那个 Callable（`Shutdown` 时要用它 `-=`）。</summary>
	private Callable _tick;
	private bool _connected;

	/// <summary>自建勾选框。</summary>
	private CheckBox _box;

	/// <summary>游戏自带「加速」复选框（每帧重新定位，可能整场战斗被重建）。</summary>
	private CheckBox _vanilla;

	/// <summary>防重入：我们自己在代码里改 `ButtonPressed` 时不要走回调。</summary>
	private bool _suppress;

	/// <summary>当前是否处于"双倍"状态。</summary>
	private bool _on;

	/// <summary>本场战斗的"原加速时间缩放"，用来算双倍值（改变时打一条日志）。</summary>
	private double _lastLoggedBase = -1.0;

	/// <summary>只报一次的小状态标记。</summary>
	private bool _reportedNoVanilla;
	private bool _reportedCreated;

	/// <summary>布局核对只报一次（v1.0.1，用于确认偏移算对）。</summary>
	private bool _layoutReported;

	/// <summary>文本刷新节流计数（v1.0.3：每 30 帧重算一次实际倍率文本）。</summary>
	private int _textTick;

	// ---------------- 三档选择器（样式 1/2/3） ----------------

	/// <summary>自建三档选择器根节点。</summary>
	private Control _sel;

	/// <summary>选择器的格子按钮（样式 1/2 = 3 个；样式 3 = 1 个）。</summary>
	private readonly System.Collections.Generic.List<Button> _cells
		= new System.Collections.Generic.List<Button>();

	/// <summary>当前档位：0 = 正常速度，1 = 游戏加速，2 = 本 Mod 加速。</summary>
	private int _gear;

	/// <summary>切换档位的快捷键（配置面板可改，默认 V）。</summary>
	private const string KHotkeyNext = "hotkeyNext";
	private const string KShowVanilla = "showVanillaCheckBox";
	private Callable _hotkeyTick;
	private bool _hotkeyPrev;
	private bool _hotkeyConnected;
	/// <summary>上一帧是否处于"战斗运行中"（v1.4.6：过关/离开后重置档位的下降沿检测）。</summary>
	private bool _wasRunning;
	/// <summary>档位是否已经真正设过一次 —— 用来区分初始的 0 和用户点了档位 0。</summary>
	private bool _gearInit;

	/// <summary>配置页样式演示图注入（懒加载一次）。</summary>
	private bool _styleDemoInjected;

	/// <summary>配置变化订阅标志（v1.4.3 热更新样式）。</summary>
	private bool _settingsSubscribed;

	/// <summary>
	/// ★ v1.4.3：配置变化热处理 —— style 改动 ⇒ 拆旧选择器/复选框，下一帧按新样式重建（免重启）。
	/// </summary>
	private void OnSettingsChangedHot(IReadOnlyList<string> keys)
	{
		try
		{
			if (keys == null) { return; }
			bool styleChanged = false;
			foreach (string k in keys)
			{
				if (k == "style") { styleChanged = true; break; }
			}
			if (styleChanged)
			{
				ApplyStyleSetting();          // 重读 Style
				_selBuilt = false;            // 下一帧重建选择器
				HideSelector();
				// ★ v1.4.4 修复：必须**连旧选择器一起拆**——只拆 _box 的话 _sel 还活着，
				//   EnsureSelector 判 _sel 有效直接早退 ⇒ 永远不按新样式重建（用户实测无效的根因）。
				if (_sel != null && GodotObject.IsInstanceValid(_sel)) { _sel.QueueFree(); }
				_sel = null;
				_cells.Clear();
				if (_box != null && GodotObject.IsInstanceValid(_box)) { _box.QueueFree(); _box = null; }
				// 游戏原生框显隐交由下一帧 OnProcessFrame 按新样式+开关决定
				Diag("样式已热更新：" + Style);
			}
		}
		catch (Exception ex) { Warn("样式热更新失败：" + ex.Message); }
	}

	/// <summary>选择器创建日志只报一次。</summary>
	private bool _selBuilt;

	/// <summary>宽度诊断只报一次。</summary>
	private bool _widthReported;

	/// <summary>是否曾经把游戏自带复选框藏起来过（未开始/离场时要还原成原样）。</summary>
	private bool _hidVanilla;

	/// <summary>藏起来之前，游戏自带复选框**本来**是不是可见的（还原用）。</summary>
	private bool _vanillaPrevVisible = true;

	/// <summary>复用的 StyleBox（按档位 3 个"亮"+3 个"暗"，避免每帧 new）。素材/代码两种模式共用。</summary>
	private StyleBox[] _sbGear;
	private StyleBox[] _sbGearDim;
	private StyleBoxFlat _sbFrameFlat;
	private StyleBoxTexture _sbFrameArt;

	/// <summary>素材贴图（从包解压根目录的 Resources/ 读，不用 res:// 映射）。</summary>
	private Texture2D _texFrame;
	private readonly System.Collections.Generic.Dictionary<string, Texture2D> _texCell
		= new System.Collections.Generic.Dictionary<string, Texture2D>();
	private readonly Texture2D[] _texArrow = new Texture2D[3];
	private bool _artTried;
	private bool _artOk;

	/// <summary>当前样式素材的 1× 尺寸（EnsureArt 里取；供布局直接使用）。</summary>
	private float _artW = 110f;
	private float _artH = 36f;
	/// <summary>素材模式下算出的格高。</summary>
	private float cellMaxH = 28f;

	/// <summary>素材是否可用（UseArt 且 11 张图都读到了）。</summary>
	private bool ArtOn
	{
		// ★★ 2026-10-13（用户实测"改样式没效果、整半天"）：
		//   素材（11 张箭头底图）是**按"三格横排"这一个样式画的**，格子朝向/尺寸全由它决定 ⇒
		//   素材模式下无论选哪种样式，画出来都是同一套图（代码绘制的竖排/单格分支根本不执行）✗。
		//   所以**只在默认样式（三格横排）下用素材**；选其它样式时强制走代码绘制 ⇒ 样式才真的看得出来。
		get { return UseArt && _artOk; }   // ★ 撤回上一轮的"仅三格横排用素材"：用户指出素材本就是通用的
	}

	/// <summary>
	/// 类型查找缓存（v1.0.3）。
	/// `VanillaAccelScale()` 现在要按帧调用（算文本），而它走 `GetSingleton()`
	/// ⇒ `AppDomain.GetAssemblies()` + `Assembly.GetType()` 每帧跑一遍太浪费。
	/// 这里缓存"已找到"的类型；**只缓存成功结果**，避免在程序集尚未加载时把 null 缓存住。
	/// </summary>
	private static readonly System.Collections.Generic.Dictionary<string, Type> _typeCache
		= new System.Collections.Generic.Dictionary<string, Type>();

	// ================================================================ 入口三回调

	
	// ============================================================ 配置面板接线
	//
	// mod.json 里声明了 enableLog（bool，默认 false）。
	// 这里在 Initialize 读一次 —— 老宿主没有 context.Settings 时吞异常、按 false 处理。
	// 用 Get<object> 而不是 Get<bool>：宿主把值存成 Dictionary<string, object>，
	// 直接 Get<bool> 可能抛 InvalidCastException。

	private static bool ReadLogSetting(XWModRuntimeContext context)
	{
		try
		{
			if (context == null)
			{
				return false;
			}
			object v = context.Settings.Get<object>("enableLog");
			if (v is bool b)
			{
				return b;
			}
			if (v is string s && bool.TryParse(s, out bool sb))
			{
				return sb;
			}
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>读 bool 配置（v1.4：showVanillaCheckBox 等新开关）。</summary>
	private bool ReadBoolSetting(string key, bool fallback)
	{
		try
		{
			if (_context == null) { return fallback; }
			object v = _context.Settings.Get<object>(key);
			if (v is bool b) { return b; }
			if (v is string s && bool.TryParse(s, out bool sb)) { return sb; }
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.True) { return true; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.False) { return false; }
			}
			return fallback;
		}
		catch { return fallback; }
	}

	private static readonly System.Collections.Generic.HashSet<string> _oneShotWarns
		= new System.Collections.Generic.HashSet<string>();

public void Initialize(XWModRuntimeContext context)
	{
			try { SponsorQrRoot = context.PackageRoot; } catch { }
			ApplyStyleSetting();   // 面板里的样式（restart 模式 ⇒ 下次启动生效）
			ApplyPositionSetting();   // 位置设置（immediate）

			EnableDiagLog = false;   // ★ 2026-10-14 用户要求：默认关闭所有日志（原：EnableDiagLog = ReadLogSetting(context)）

		try
		{
			_context = context;
			string root = (context == null) ? "<null>" : context.PackageRoot;
			Info("初始化完成；PackageRoot=" + root
				+ "。将在「加速」正下方新增「" + BoxText + "」勾选框（= 原加速 ×"
				+ DoubleFactor.ToString("0.##") + "）。");
		}
		catch (Exception ex)
		{
			Warn("Initialize 异常（已吞）：" + ex.Message);
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Warn("拿不到 SceneTree，功能不可用。");
				return;
			}
			// ★ 不能依赖自定义 `_Process`（本工程手写 csproj，没有 Godot 源生成器，
			//   入口类的 `_Process` 永远不会被调用）⇒ 挂 `SceneTree` 的 process_frame 信号。
			//   ⚠️ 必须用 `Connect("process_frame", callable)` 这种**字符串信号名**写法。
			//     写成 `_tree.ProcessFrame += callable` 会 CS0029：C# 侧的 `ProcessFrame`
			//     属性类型是 `Action`，而 `Callable` **不能**隐式转成 `Action`
			//     （只有 `Action → Callable` 这一个方向有隐式转换）。
			_tick = Callable.From(new Action(OnProcessFrame));
			_tree.Connect("process_frame", _tick);
			// 快捷键：独立一条 process_frame 连接，不干扰主循环
			try
			{
				if (!_hotkeyConnected)
				{
					_hotkeyTick = Callable.From(new Action(OnHotkeyTick));
					_tree.Connect("process_frame", _hotkeyTick);
					_hotkeyConnected = true;
					Info("档位快捷键已挂载：" + ReadHotkey());
				}
			}
			catch { }
			_connected = true;
			Info("已挂接 SceneTree.ProcessFrame。");
		}
		catch (Exception ex)
		{
			Warn("OnAllModsLoaded 异常（已吞）：" + ex.Message);
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_connected && _tree != null && GodotObject.IsInstanceValid(_tree)
				&& _tree.IsConnected("process_frame", _tick))
			{
				_tree.Disconnect("process_frame", _tick);
			}
			_connected = false;
			_box = null;
			_vanilla = null;
		}
		catch (Exception ex)
		{
			Warn("Shutdown 异常（已吞）：" + ex.Message);
		}
	}

	// ================================================================ 每帧

	/// <summary>★ v1.4.6：每帧样式对齐检查——settings 的 style 与 Style 枚举不一致 ⇒ 热重建。</summary>
	private void CheckStyleSync()
	{
		try
		{
			// ★ 性能：本方法是"宿主 Changed 事件可能没触发"的**兜底**（正式的订阅在 OnProcessFrame 里，
			//   `Settings.Changed += OnSettingsChangedHot`）⇒ 不必每帧轮询，15 帧足够。
			//   顺带每帧的 `Get<object>` 装箱 + `JsonElement.ToString()` 字符串分配也降到 1/15。
			ulong _csF = Engine.GetProcessFrames();
			if (_csF - _styleSyncFrame < 15UL) { return; }
			_styleSyncFrame = _csF;
			if (_context?.Settings == null) { return; }
			object v = _context.Settings.Get<object>("style");
			string s = null;
			if (v is string str) { s = str; }
			else if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String) { s = je.GetString(); }
			else if (v is System.Text.Json.JsonElement jn) { s = jn.ToString(); }
			if (string.IsNullOrWhiteSpace(s)) { return; }
			s = s.Trim();
			BtnStyle want = Style;
			if (s == "1" || s.Equals("tri", StringComparison.OrdinalIgnoreCase)) { want = BtnStyle.TriHorizontal; }
			else if (s == "2" || s.Equals("vert", StringComparison.OrdinalIgnoreCase)) { want = BtnStyle.TriVertical; }
			else if (s == "3" || s.Equals("single", StringComparison.OrdinalIgnoreCase)) { want = BtnStyle.SingleGear; }
			else if (s == "4" || s.Equals("checkbox", StringComparison.OrdinalIgnoreCase)) { want = BtnStyle.CheckBox; }
			if (want != Style)
			{
				Style = want;
				_selBuilt = false;
				HideSelector();
				if (_sel != null && GodotObject.IsInstanceValid(_sel)) { _sel.QueueFree(); }
				_sel = null;
				_cells.Clear();
				if (_box != null && GodotObject.IsInstanceValid(_box)) { _box.QueueFree(); _box = null; }
				Diag("样式对齐（每帧检查）：" + Style);
				// ★ 2026-10-13 诊断：到底卡在哪一步（重建前/后控件的存在情况）
				try
				{
					Diag("样式重建状态：sel=" + ((_sel == null) ? "null" : (_selBuilt ? "built" : "pending"))
						+ " box=" + ((_box == null) ? "null" : "ok")
						+ " cells=" + _cells.Count + " inBattle=" + (_tree != null));
				}
				catch { }
			}
		}
		catch { }
	}


	private void OnProcessFrame()
	{
		try { SponsorQrInjector.Tick(Engine.GetMainLoop() as SceneTree, "三倍加速", SponsorQrRoot, "Resources/SponsorQR.png"); } catch { }
		// ★ v1.4.6：每帧对齐样式设置（防宿主 Changed 事件丢失/未触发——用户实测热更新无效）
		try { CheckStyleSync(); } catch { }
		// ★ 样式重建重试：素材可能晚一两帧才就绪 ⇒ 就绪后再按当前样式重建一次（否则会停在代码绘制）
		try
		{
			if (_styleRebuildRetry > 0)
			{
				_styleRebuildRetry--;
				CheckBox van2 = VanillaBoxCached();
				try { EnsureArt(); } catch { }
				if (ArtOn)
				{
					if (_sel != null && GodotObject.IsInstanceValid(_sel)) { _sel.QueueFree(); }
					_sel = null;
					_selBuilt = false;
					EnsureSelector(van2);
					SyncSelector(van2);
					_styleRebuildRetry = 0;
					Diag("素材就绪后已按样式重建（ArtOn=" + ArtOn + " 样式=" + (int)Style + "）");
				}
			}
		}
		catch { }
		// ★ v1.4.3：订阅配置变化 —— 样式改动后**不用重启**（拆旧控件，下一帧按新样式重建）；
		//   顺带恢复「录制面板 + 配置页录制按钮」（用户要求回归；无面板呼出键）
		try
		{
			if (_context?.Settings != null && !_settingsSubscribed)
			{
				_context.Settings.Changed += OnSettingsChangedHot;
				_settingsSubscribed = true;
			}
		}
		catch (Exception ex) { Warn("订阅配置失败：" + ex.Message); }
		try
		{
			string cur = ReadHotkey();
			KeybindPanelManager.Tick(_tree, "三倍加速", "三倍加速", SponsorQrRoot ?? (_context?.PackageRoot ?? ""),
				KHotkeyNext, ParseKeySettingCached(cur), string.IsNullOrWhiteSpace(cur) ? "（无绑定）" : cur,
				key => WriteHotkeySetting(key));
		// 注：面板保存后配置页下拉在重新打开时同步（宿主 UI 无外部刷新 API）
		}
		catch (Exception ex) { Warn("键位面板异常：" + ex.Message); }
		// ★ v1.4：样式演示图注入配置页（懒加载）
		try { TickStyleDemoImage(); } catch { }
		// ★ 需求 4+6（2026-10-13）：战斗设置页里的「三倍加速 · 位置」编辑器。
		//   本 Mod 原本不碰设置页，所以这里用"15 帧一次的轻量守候"：原生 find_child 找
		//   BattleOption 对话框，找到就交给共享组件建/刷新那一行（幂等、开销可忽略）。
		try
		{
			ulong _peF = Engine.GetProcessFrames();
			if (_peF - _posEditorFrame >= 15UL)
			{
				_posEditorFrame = _peF;
				Node bo = (_tree != null && GodotObject.IsInstanceValid(_tree) && _tree.Root != null)
					? _tree.Root.FindChild("BattleOption", true, false) : null;
				if (bo != null && GodotObject.IsInstanceValid(bo)) { EnsurePosEditor(bo); }
			}
		}
		catch { }
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			CheckBox vanilla = VanillaBoxCached();   // ★ 性能：不再每帧整树找
			if (vanilla == null)
			{
				// 不在战斗场景（主菜单、关卡选择…）⇒ 收起自己的按钮
				if (_box != null && GodotObject.IsInstanceValid(_box))
				{
					_box.Visible = false;
				}
				HideSelector();
				if (!_reportedNoVanilla)
				{
					_reportedNoVanilla = true;
					Diag("未找到游戏自带「加速」复选框（当前不在战斗场景）⇒ 本勾选框隐藏。");
				}
				return;
			}
			_reportedNoVanilla = false;
			_vanilla = vanilla;

			// ★ 用户要求（2026-10-09）：**游戏没正式开始时不显示**。
			//   判定用游戏自己的 `TowerDefenseManager.Instance.IsGameRunning()`
			//   （全工程 121 处在用，选卡/关卡预览阶段为 false）。
			//   未开始时：收起自建控件，并把游戏自带复选框**还原成我们动它之前的样子**。
			if (!IsGameRunning())
			{
				HideSelector();
				HideCheckBox();
				RestoreVanillaVisibility(vanilla);
				// ★ v1.4.6（用户 2026-10-11）：**过关/失败/离开关卡后档位不重置**的 bug——
				//   检测「战斗从运行中变为非运行」的下降沿 ⇒ 重置档位为 0（正常速度） +
				//   还原游戏原生 TimeScale，下次进关从正常速度开始。
				if (_wasRunning)
				{
					_wasRunning = false;
					if (_gear != 0)
					{
						int oldGear = _gear;
						_gear = 0;
						_on = false;
						_gearInit = false;   // 下关首次切档不受"同档早退"影响
						SetVanillaPressed(false);
						SetTimeScaleRaw(BaseTimeScale());
						SyncCells();
						Diag("战斗结束（离开/过关/失败），档位已从 " + GearName[oldGear] + " 重置为正常速度。");
					}
				}
				return;
			}
			_wasRunning = true;

			// ★ v1.4：「显示游戏自带加速复选框」开关（默认关 = 隐藏；开 = 保留原生框可见）。
			//   样式 4 下本开关不生效（那种样式本来就以原生框为主）。
			bool showVanilla = ReadBoolSetting(KShowVanilla, false);
			if (Style != BtnStyle.CheckBox && !showVanilla && vanilla.Visible)
			{
				vanilla.Visible = false;
				try { _hidVanilla = true; } catch { }   // ★ 记录"是我们隐藏的"，切回样式 4 时才能正确还原
			}

			if (Style == BtnStyle.CheckBox)
			{
				// 样式 4：原有复选框（且已把与「加速」的间距拉短）
				// ★★ 2026-10-13（用户实测截图：选"4 · 原有复选框"时控件是空的）：
				//   之前从样式 1/2/3 切到 4 时，**只隐藏、不还原**游戏原版复选框
				//   （还原只发生在退出/关 Mod 时，见 RestoreVanillaVisibility）⇒ 样式 4 只剩一个空壳 ✗。
				//   这里在进入样式 4 前先把原版复选框**放回来**。
				try { RestoreVanillaVisibility(vanilla); } catch { }
				if (vanilla != null && GodotObject.IsInstanceValid(vanilla) && !vanilla.Visible)
				{
					vanilla.Visible = true;   // 兜底：即便标记位不一致也让它可见（样式 4 就是用它）
				}
				HideSelector();
				EnsureBox(vanilla);
				SyncBox(vanilla);
			}
			else
			{
				// 样式 1/2/3：自建三档选择器（默认隐藏原生复选框，见上方开关）
				EnsureSelector(vanilla);
				SyncSelector(vanilla);
			}
			EnforceMutualExclusion();
		}
		catch (Exception ex)
		{
			Warn("每帧维护异常（只报一次）：" + ex.Message);
		}
	}

	// ================================================================ 样式演示图（配置页关于栏内嵌）

	private Texture2D _styleDemoTex;

	/// <summary>
	/// ★ v1.4：把四档样式演示图注入**本 Mod 配置页**的「样式演示图」富文本。
	/// 锚 = RichTextLabel（名字 Content_aboutStyleImages，宿主按 "Content_"+key 命名），
	/// 归属判定：Text 以「样式说明（宿主面板不支持图片」开头的是本 Mod 的（共享注入器同款机制）。
	/// </summary>
	private void TickStyleDemoImage()
	{
		try
		{
			// ★ 性能（2026-10-12 真机探针）：本方法**每帧**被调用，内部是**两次整树递归查找**
			//   （`_tree.Root` 上找 Content_aboutStyleImages 与 Reset_hotkeyNext）。
			//   真机采样：本 Mod 回调均 4.44ms/帧、中位 4.31ms。而配置页的出现/消失
			//   不可能发生在 20 帧内 ⇒ 每 20 帧查一次，行为等价。
			ulong _sdF = Engine.GetProcessFrames();
			if (_sdF - _styleDemoFrame < 20UL) { return; }
			_styleDemoFrame = _sdF;
			if (_styleDemoInjected) { return; }
			// ★ 性能：改用引擎原生 find_child（C++ 内搜索），比 C# 逐节点递归快一个数量级
			Node rich = _tree.Root.FindChild("Content_aboutStyleImages", true, false);
			if (rich == null || !GodotObject.IsInstanceValid(rich) || rich is not RichTextLabel rtl) { return; }
			// 归属：本 Mod 的该项以「样式说明（」起头（aboutStyles 的 text）——不对，那是另一项；
			// 本项（演示图）的文本以「四档按钮样式演示图」前缀判定不可靠（我们 AddImage 前文本是说明文字）。
			// 稳妥法：本 Mod 配置页的特征 = 同页存在 Reset_hotkeyNext（只有本 Mod 声明它）。
			if (_tree.Root.FindChild("Reset_hotkeyNext", true, false) == null) { return; }

			if (_styleDemoTex == null || !GodotObject.IsInstanceValid(_styleDemoTex))
			{
				string root = SponsorQrRoot ?? (_context?.PackageRoot ?? "");
				if (string.IsNullOrEmpty(root)) { return; }
				string p = System.IO.Path.Combine(root, "Resources", "StyleDemo.png");
				if (!System.IO.File.Exists(p)) { return; }
				Image img = Image.LoadFromFile(p);
				if (img == null) { return; }
				_styleDemoTex = ImageTexture.CreateFromImage(img);
			}
			float w = 460f;
			float h = _styleDemoTex.GetHeight() > 0 ? w * _styleDemoTex.GetHeight() / _styleDemoTex.GetWidth() : w;
			rtl.AddImage(_styleDemoTex, w, h, null, InlineAlignment.Center);
			_styleDemoInjected = true;
			Diag("已在配置页关于栏显示样式演示图。");
		}
		catch (Exception ex) { WarnOnceS("styledemo", ex.Message); }
	}

	private void WarnOnceS(string tag, string msg)
	{
		try { if (_oneShotWarns.Add(tag)) { Warn(msg); } } catch { }
	}

	/// <summary>
	/// 找游戏自带「加速」复选框。
	///
	/// 路径取自 `Scene/TowerDefesne/TowerDefenseNew/TowerDefenseControlNew.tscn`：
	///     [node name="CheckBox2X" type="CheckBox" parent="GUITop"]
	/// 但**不写死整条路径** —— 逐级按类名找 `TowerDefenseControl` 节点更稳
	/// （关卡编辑器等场景可能换皮），拿到后再按 `checkBox2X` 字段名找子节点。
	/// </summary>
	// ── ★ 性能（2026-10-12）：原实现**每帧**调 FindVanillaBox() = 每帧一次整树递归 + 反射，
	//    安卓上约 3~5ms/帧。改为缓存：命中后 60 帧复查一次，未命中时 10 帧重试
	//    （未命中=不在战斗场景，缩到 10 帧是为了让"进关卡后按钮出现"的延迟不被拉长）。
	private CheckBox _vanillaCache;
	private ulong _vanillaCacheFrame;

	private CheckBox VanillaBoxCached()
	{
		try
		{
			ulong f = Engine.GetProcessFrames();
			if (_vanillaCache != null && GodotObject.IsInstanceValid(_vanillaCache) && _vanillaCache.IsInsideTree())
			{
				if (f - _vanillaCacheFrame < 60UL) { return _vanillaCache; }
				_vanillaCacheFrame = f;
				_vanillaCache = FindVanillaBox();
				return _vanillaCache;
			}
			if (f - _vanillaCacheFrame < 10UL) { return null; }
			_vanillaCacheFrame = f;
			_vanillaCache = FindVanillaBox();
			return _vanillaCache;
		}
		catch { return null; }
	}

	private CheckBox FindVanillaBox()
	{
		try
		{
			Node ctl = FindNodeByClassName(_tree.Root, "TowerDefenseControl");
			if (ctl == null || !GodotObject.IsInstanceValid(ctl))
			{
				return null;
			}
			// ① 首选：反射读 `checkBox2X` 字段（TowerDefenseControl.cs L10 是 public 字段）
			try
			{
				FieldInfo f = ctl.GetType().GetField("checkBox2X",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (f != null && f.GetValue(ctl) is CheckBox cb && GodotObject.IsInstanceValid(cb))
				{
					return cb;
				}
			}
			catch { }
			// ② 兜底：按节点名找
			return FindNodeByName(ctl, "CheckBox2X") as CheckBox;
		}
		catch { return null; }
	}

	/// <summary>★ 性能：样式演示图的整树查找节流（见 TickStyleDemoImage）。</summary>
	private ulong _styleDemoFrame;

	/// <summary>★ 性能：CheckStyleSync 的轮询节流（见该方法）。</summary>
	private ulong _styleSyncFrame;

	/// <summary>创建（或找回）自建勾选框；父节点与「加速」相同，保证同一套主题与变换。</summary>
	private void EnsureBox(CheckBox vanilla)
	{
		try
		{
			if (_box != null && GodotObject.IsInstanceValid(_box))
			{
				return;
			}
			Node parent = vanilla.GetParent();
			if (parent == null || !GodotObject.IsInstanceValid(parent))
			{
				return;
			}
			// 场上可能已有一个（例如本场景重建后我们丢了引用）⇒ 先找回来，避免叠加两个
			Node existing = FindNodeByName(parent, NewBoxName);
			if (existing is CheckBox found && GodotObject.IsInstanceValid(found))
			{
				_box = found;
				WireBox(_box);
				return;
			}

			CheckBox box = new CheckBox();
			box.Name = NewBoxName;
			box.Text = BoxInitText;   // 真实倍率随后由 SyncBox 按关卡实际值覆盖
			box.ToggleMode = true;
			box.ButtonPressed = _on;          // 跨场景保留用户的选择
			box.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
			// 与「加速」同款：字号 24 + 描边 5（原节点上是 theme_override_*，
			// 父节点 GUITop 没有这两项覆盖，所以要自己复制一份）
			try
			{
				box.AddThemeFontSizeOverride("font_size", 24);
				box.AddThemeConstantOverride("outline_size", 5);
			}
			catch { }
			// ⚠️ 锚点**不在这里设**：`SyncBox()` 每帧会把本框的 4 个锚点复制成与「加速」完全一致，
			//   再按偏移差定位（v1.0.1 修正 —— 见 SyncBox 里的长注释）。
			//   这里只给一个初始尺寸，确保 AddChild 时不是零尺寸。
			box.CustomMinimumSize = new Vector2(104f, 34f);
			// ★ v1.0.5：创建时就把 scale 对齐原版（原版带 scale 0.78），
			//   否则第一帧会以全尺寸闪一下（虽然 SyncBox 随后就会改过来）。
			try
			{
				box.Scale = vanilla.Scale;
			}
			catch { }
			box.Visible = false;              // 等 SyncBox 定位好再显示，避免闪一下
			parent.AddChild(box);
			_box = box;
			WireBox(_box);
			if (!_reportedCreated)
			{
				_reportedCreated = true;
				Info("已创建「" + BoxText + "」勾选框（父节点=" + parent.Name + "）。");
			}
		}
		catch (Exception ex)
		{
			Warn("创建勾选框失败：" + ex.Message);
		}
	}

	/// <summary>挂 `Toggled`。Godot 4 无 `Callable.Bind()`，用闭包现造 Callable。</summary>
	private void WireBox(CheckBox box)
	{
		try
		{
			if (box == null || !GodotObject.IsInstanceValid(box))
			{
				return;
			}
			if (box.HasMeta("ds_wired"))
			{
				return;
			}
			box.SetMeta("ds_wired", true);
			// ⚠️ `Toggled` 是 C# event，委托类型是 `BaseButton.ToggledEventHandler`；
			//   直接传方法组即可，**不要**包成 `new Action<bool>(...)`（会 CS0029）。
			box.Toggled += OnNewBoxToggled;
		}
		catch (Exception ex)
		{
			Warn("挂 Toggled 失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 每帧把自建勾选框摆到「加速」正下方，并与它的可见性保持一致。
	///
	/// 定位用 `offset_*` 而不是 `GlobalPosition`：两者同父、同锚点（右上角），
	/// offset 天然跟随窗口缩放与父节点变换，不需要自己做坐标换算。
	///
	/// ⚠️ 「加速」带 `scale = (0.78, 0.78)`（tscn 实测）。`Control.Size` 是**未缩放**尺寸、
	///   `Position` 也是未缩放的布局位置，而**视觉**右边缘 = `Position.X + Size.X * scale.X`。
	///   本框不加 scale（scale=1，文字更清晰），所以宽度按视觉宽度来留。
	/// </summary>
	private void SyncBox(CheckBox vanilla)
	{
		try
		{
			CheckBox box = _box;
			if (box == null || !GodotObject.IsInstanceValid(box))
			{
				return;
			}
			bool vVis = vanilla.Visible;
			if (box.Visible != vVis)
			{
				box.Visible = vVis;
			}
			if (!vVis)
			{
				return;
			}

			float vScaleX = 1f, vScaleY = 1f;
			try
			{
				vScaleX = vanilla.Scale.X;
				vScaleY = vanilla.Scale.Y;
			}
			catch { }
			if (vScaleX <= 0.01f) { vScaleX = 1f; }
			if (vScaleY <= 0.01f) { vScaleY = 1f; }

			Vector2 vSize = vanilla.Size;                    // 未缩放布局尺寸

			// ★★★ v1.0.3（用户要求）：**文本显示实际倍率** ——
			//   「加速」→ 显示它的真实倍率，「本框」→ 显示本框的真实倍率。
			//
			//   ⚠️ 不能写死 "1.5x" / "3x"：真实倍率**随关卡变化**（源码实测）：
			//        Global.TimeScale = baseTimeScale × 1.5   （普通关）
			//        Global.TimeScale = baseTimeScale × 3.0   （finishMethod == QUIZ）
			//      而 `baseTimeScale` 默认 1.0，**但有 14 个关卡显式写了 2.0**：
			//        MiniGames_Level13_1~13_6（+6 个 _D）、Challenge_Level_Diamond7_2（+_D）
			//      ⇒ 那些关卡里：「加速」实际是 3x、本框是 6x。
			//      所以必须**每帧按实际算**，才会"数值按照实际的来"。
			//      （普通关卡上算出来正好就是 1.5x / 3x。）
			//
			//   节流：每 30 帧算一次即可（关卡配置在战斗中不会变），避免每帧反复反射。
			_textTick++;
			if (_textTick >= 30 || _textTick == 1)
			{
				_textTick = 0;
				double accel = VanillaAccelScale();              // 「加速」的实际倍率
				double mine = accel * DoubleFactor;              // 本框的实际倍率
				string accelText = FmtX(accel);
				string mineText = FmtX(mine);
				if (vanilla.Text != accelText)
				{
					vanilla.Text = accelText;
				}
				if (box.Text != mineText)
				{
					box.Text = mineText;
				}
			}

			// ★★★ v1.0.5（用户要求"有点大，改小，整体大小与原版一致"）：
			//   **连 scale 一起复制原版**，并把布局尺寸也用原版的。
			//
			//   为什么之前偏大：原版 `CheckBox2X` 在 tscn 里带 `scale = 0.78`，
			//   所以它的字号 24 / 描边 5 实际渲染成 24×0.78 ≈ 18.7，
			//   连勾选框方块（图标）也一起缩到 0.78。
			//   我此前把本框 scale 留成 1（想让文字更清晰）⇒ 方块与文字都比原版大一圈。
			//   ⇒ 现在 scale 完全对齐，尺寸也取原版的**布局尺寸**，
			//     两个框在视觉上就是同一套大小（只有文字内容不同）。
			box.Scale = vanilla.Scale;
			try
			{
				box.Alignment = vanilla.Alignment;   // 文字对齐也照抄，避免居中/靠左不一致
			}
			catch { }

			// 布局尺寸 = 原版布局尺寸（未缩放）；视觉尺寸 = 布局 × scale
			float myW = Math.Max(1f, vSize.X);
			float myH = Math.Max(1f, vSize.Y);
			float myVisW = myW * vScaleX;

			// ★★★ v1.0.2→v1.0.4：**放在「加速」正下方**，并让「时停」自动让位。
			//
			//   ⚠️ 关键前提（读 TimeStop 源码得到的硬事实）：
			//      「时停」原本固定锚在「加速」正下方：
			//          // TimeStopEntry.cs
			//          Control anchorBtn = FindAnchorButton(root) ?? FindGearFallback(root);
			//          //   ↑ 优先返回"加速按钮 checkBox2X"，找不到才退回齿轮 optionButton
			//          pc.GlobalPosition = new Vector2(aPos.X, aPos.Y + aSize.Y + 6f);
			//      ⇒ "加速正下方"这个位置原本被时停占用。
			//      自 TimeStop v1.0.31 起，它会**在加速下方找底边最低的 Mod 控件并锚到它之下**
			//      ⇒ 本框可以紧贴加速，时停自动排到本框之后。
			//      最终顺序：加速 / 本框 / 时停。
			//
			//   ── 为什么用 GlobalPosition（而不是 offset 差）────────────────
			//      「时停」的父节点与本框**不同**，两者 offset 不在同一坐标系、无法相减比较
			//      ⇒ 必须统一到**全局坐标**。TimeStop 自己也是直接写 `GlobalPosition`
			//      （本工程已验证可行）；本方法每帧重算，不依赖锚点自适应。
			//
			//   锚点仍然复制成与「加速」一致：这样即使本帧还没走到下面的 GlobalPosition 赋值，
			//   布局基准也是对的（不会在别处闪一下）。
			box.AnchorLeft = vanilla.AnchorLeft;
			box.AnchorRight = vanilla.AnchorRight;
			box.AnchorTop = vanilla.AnchorTop;
			box.AnchorBottom = vanilla.AnchorBottom;
			box.GrowHorizontal = Control.GrowDirection.Begin;
			box.GrowVertical = Control.GrowDirection.Begin;

			Vector2 vGlobal = vanilla.GlobalPosition;        // 「加速」视觉左上角（scale 绕 pivot，原点即左上）
			float vVisW = vSize.X * vScaleX;
			float vVisH = vSize.Y * vScaleY;
			float vRightX = vGlobal.X + vVisW;               // 「加速」视觉右边缘

			// 与「加速」**右对齐**（两者都贴右侧留白；本框文字更短，宽度下限已收窄）
			// 视觉右边缘与原版对齐 ⇒ 用**视觉宽度**（= 布局宽 × scale）
			float myLeftG = vRightX - myVisW;
			// 紧贴「加速」视觉下边缘 —— ★ v1.0.4：**不再避让「时停」**。
			//
			//   ⚠️ 这里必须与 TimeStop v1.0.31 的改动**成对**：
			//      旧方案：本框主动下移到「时停」之下  ⇒ 顺序是 加速 / 时停 / 本框
			//      新方案：TimeStop 改成"锚到加速下方**最底部**的 Mod 控件"
			//              ⇒ 本框**紧贴加速**，时停自动排到本框之下 ⇒ 顺序是 加速 / 本框 / 时停
			//
			//   ⚠️⚠️ 若只改一边就会**互相追逐**：本框避让时停往后挪、时停又锚到本框之下，
			//     两者每帧各下移一次 ⇒ 一路滑出屏幕。所以这两处必须同时生效。
			//     （用户要求："将两个加速框放上下一起" —— 即 1.5x 与 3x 相邻。）
			float myTopG = vGlobal.Y + vVisH + VGapPx;

			// ★★ 2026-10-13（用户实测："复选框样式没法设定位置"）：
			//   这里原来**只有"贴加速下方"这一种算法**（`SyncSelector` 那边才有 `_posCustom` 分支）✗
			//   ⇒ 选「4 · 原有复选框」时，位置下拉怎么改都不动。
			//   现在同样支持自定义百分比坐标（与另外三种样式一致）。
			if (_posCustom)
			{
				Vector2 vpBox = GetViewportSizeForPos();
				myLeftG = vpBox.X * Mathf.Clamp(_posX, 0, 100) / 100f;
				myTopG = vpBox.Y * Mathf.Clamp(_posY, 0, 100) / 100f;
			}

			// ⚠️ 必须**显式设尺寸**：只设 GlobalPosition 时，Control 的尺寸仍由
			//   锚点+offset 决定（创建时都是 0）⇒ 实际尺寸会退化成 CustomMinimumSize，
			//   与我算出的 myW/myH 不一致，右对齐就会偏。
			//   **先设尺寸、后设位置**：设尺寸可能因锚点而带动位置，位置必须最后定。
			box.CustomMinimumSize = new Vector2(myW, myH);
			box.Size = new Vector2(myW, myH);
			box.GlobalPosition = new Vector2(myLeftG, myTopG);

			// 一次性诊断：把两边的锚点/偏移/尺寸/全局位置都打出来，便于核对（受 EnableDiagLog 门控）
			if (!_layoutReported)
			{
				_layoutReported = true;
				Diag("布局核对："
					+ "vanilla[anchor=(" + vanilla.AnchorLeft + "," + vanilla.AnchorTop + ","
					+ vanilla.AnchorRight + "," + vanilla.AnchorBottom + ")"
					+ " offset=(" + vanilla.OffsetLeft.ToString("0.#") + "," + vanilla.OffsetTop.ToString("0.#")
					+ "," + vanilla.OffsetRight.ToString("0.#") + "," + vanilla.OffsetBottom.ToString("0.#") + ")"
					+ " gpos=" + vGlobal + " size=" + vSize + " scale=" + vanilla.Scale + "]"
					+ " ｜ mine[gpos=(" + myLeftG.ToString("0.#") + "," + myTopG.ToString("0.#")
					+ ") size=(" + myW.ToString("0.#") + "," + myH.ToString("0.#")
					+ ") 实际=" + box.GlobalPosition + " visible=" + box.Visible + "]"
					+ "（v1.0.4：本框紧贴「加速」下方；「时停」由 TimeStop v1.0.31 自动排到本框之下）");
			}
		}
		catch { }
	}

	// ================================================================ 开关逻辑

	/// <summary>用户点了自建勾选框。</summary>
	private void OnNewBoxToggled(bool pressed)
	{
		try
		{
			if (_suppress)
			{
				return;
			}
			// 样式 4 只有两态：勾上 = 档位 2（Mod 加速）、取消 = 档位 0（正常速度）
			// （档位 1 = 游戏加速由游戏自带复选框自己负责）
			ApplyGear(pressed ? 2 : 0);
		}
		catch (Exception ex)
		{
			Warn("切换异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>
	/// 读取「原加速」那一档对应的时间缩放（= `baseTimeScale * 1.5`，QUIZ 关为 `* 3.0`）。
	/// 这是"双倍"的基数；取不到时退化用 1.5。
	/// </summary>
	private double VanillaAccelScale()
	{
		// 直接由 BaseTimeScale 推导（它内部已吞异常），这里不必再包 try
		return BaseTimeScale() * (IsQuizLevel() ? 3.0 : 1.5);
	}

	/// <summary>
	/// 本关的 `baseTimeScale`（默认 1.0；**有 14 个关卡显式写了 2.0**：
	/// MiniGames_Level13_1~13_6（+_D）、Challenge_Level_Diamond7_2（+_D））。
	/// 档位 0（正常速度）用的就是它。
	/// </summary>
	private double BaseTimeScale()
	{
		double baseTs = 1.0;
		try
		{
			object cfg = null;
			object mgr = GetSingleton("TowerDefenseManager");
			if (mgr != null)
			{
				cfg = GetMember(mgr, "currentLevelConfig");
				object ctl = GetMember(mgr, "currentControl");
				if (cfg == null && ctl != null)
				{
					cfg = GetMember(ctl, "levelConfig");
				}
			}
			if (cfg != null)
			{
				object b = GetMember(cfg, "baseTimeScale");
				if (b is double bd) { baseTs = bd; }
				else if (b is float bf) { baseTs = bf; }
			}
		}
		catch { }
		if (baseTs <= 0.0) { baseTs = 1.0; }
		return baseTs;
	}

	private bool IsQuizLevel()
	{
		try
		{
			object mgr = GetSingleton("TowerDefenseManager");
			if (mgr == null)
			{
				return false;
			}
			object m = GetMember(mgr, "currentLevelConfig");
			if (m == null)
			{
				return false;
			}
			object fm = GetMember(m, "finishMethod");
			if (fm == null)
			{
				return false;
			}
			return fm.ToString() == "QUIZ";
		}
		catch { return false; }
	}

	/// <summary>把 `Global.TimeScale` 设为「原加速档位 × factor」。</summary>
	private void ApplyTimeScale(double factor)
	{
		SetTimeScaleRaw(VanillaAccelScale() * factor);
	}

	/// <summary>把 `Global.TimeScale` **直接设为给定值**（三档模型用）。
	/// ★ v1.4.5 修复：`PropertyInfo.CanWrite` 被游戏 BCL 裁剪（get_CanWrite MissingMethodException）
	///   ⇒ 旧版在检查时就抛异常 ⇒ 所有切档失败（1→3 无反应、2→3 变 1 档）。
	///   `Global.TimeScale` 是 **public static** 属性 ⇒ 改用编译期直调
	///   `Global.TimeScale = want`（交接文档铁律：能直调就别反射）。</summary>
	private void SetTimeScaleRaw(double want)
	{
		try
		{
			Global.TimeScale = want;
			if (Math.Abs(_lastLoggedBase - want) > 0.0001)
			{
				_lastLoggedBase = want;
				Diag("TimeScale 设为 " + want.ToString("0.###")
					+ "（原加速档=" + VanillaAccelScale().ToString("0.###")
					+ "，当前档位=" + _gear + "）");
			}
		}
		catch (Exception ex)
		{
			Warn("设置 TimeScale 失败：" + ex.Message);
		}
	}

	private string CurrentTimeScaleText()
	{
		try
		{
			return Engine.TimeScale.ToString("0.###");
		}
		catch { return "?"; }
	}

	/// <summary>
	/// 把游戏自带「加速」复选框设为未勾选。
	/// 直接改 `ButtonPressed` **不会**触发 `Toggled` 回调，
	/// 所以它内部记的档位状态不会自己变——但游戏每帧并不依赖那个状态，
	/// 只依赖 `Global.TimeScale`，故安全。
	/// </summary>
	private void SetVanillaPressed(bool pressed)
	{
		try
		{
			CheckBox v = _vanilla;
			if (v == null || !GodotObject.IsInstanceValid(v))
			{
				return;
			}
			if (v.ButtonPressed == pressed)
			{
				return;
			}
			_suppress = true;
			v.ButtonPressed = pressed;
		}
		catch { }
		finally { _suppress = false; }
	}

	/// <summary>
	/// 监测游戏自带「加速」被玩家自己勾上 —— 此时要把本框取消（互斥的反方向）。
	/// 用状态比较实现：若它变 true 而我们的 `_on` 也是 true，就取消我方。
	/// </summary>
	private void EnforceMutualExclusion()
	{
		try
		{
			CheckBox v = _vanilla;
			if (v == null || !GodotObject.IsInstanceValid(v))
			{
				return;
			}
			// 玩家自己动了游戏自带「加速」⇒ 档位跟着它走（游戏自己已经改过 TimeScale）
			//   · 被勾上 ⇒ 档位 1（游戏加速）
			//   · 被取消 ⇒ 档位 0（正常速度）
			bool vp = v.ButtonPressed;
			if (vp && _gear != 1)
			{
				_gear = 1;
				_on = false;
				SyncCells();
				if (_box != null && GodotObject.IsInstanceValid(_box))
				{
					_suppress = true;
					_box.ButtonPressed = false;
				}
				Info("检测到「加速」被勾选 ⇒ 档位切到 1（游戏加速）。");
			}
			else if (!vp && _gear == 1)
			{
				_gear = 0;
				SyncCells();
				Info("检测到「加速」被取消 ⇒ 档位切到 0（正常速度）。");
			}
		}
		catch { }
		finally { _suppress = false; }
	}

	// ================================================================ 三档选择器（样式 1/2/3）

	/// <summary>收起三档选择器（切到样式 4 或离开战斗时）。</summary>
	private void HideSelector()
	{
		try
		{
			if (_sel != null && GodotObject.IsInstanceValid(_sel))
			{
				_sel.Visible = false;
			}
		}
		catch { }
	}

	/// <summary>
	/// 找游戏自带「菜单」按钮（`GUITop/ButtonPause`，与 `CheckBox2X` 是**同一个父节点**的兄弟）。
	///
	/// 名字取自 `Scene/TowerDefesne/TowerDefenseNew/TowerDefenseControlNew.tscn`：
	///     [node name="ButtonPause" parent="GUITop" instance=ExtResource("6_luhhn")]
	/// 用户要求把自建控件**右对齐到这个按钮**（比对齐「加速」更贴右侧留白）。
	/// 拿不到就返回 null ⇒ 调用处退回"对齐加速复选框"的老行为。
	/// </summary>
	private Control FindPauseButton(CheckBox vanilla)
	{
		try
		{
			Node p = vanilla.GetParent();
			if (p == null || !GodotObject.IsInstanceValid(p))
			{
				return null;
			}
			Node n = FindNodeByName(p, "ButtonPause");
			if (n is Control c && GodotObject.IsInstanceValid(c))
			{
				return c;
			}
		}
		catch { }
		return null;
	}

	/// <summary>把游戏自带复选框还原成"我们动它之前"的可见性。</summary>
	private void RestoreVanillaVisibility(CheckBox vanilla)
	{
		try
		{
			if (!_hidVanilla || vanilla == null || !GodotObject.IsInstanceValid(vanilla))
			{
				return;
			}
			vanilla.Visible = _vanillaPrevVisible;
			_hidVanilla = false;
		}
		catch { }
	}

	/// <summary>
	/// 本关是否**已经正式开始**（`TowerDefenseManager.Instance.IsGameRunning()`）。
	/// 选卡 / 关卡预览阶段是 false ⇒ 那时不显示自建控件（用户要求）。
	/// ⚠️ 反射拿不到时返回 **true** —— 宁可显示，也不要因为反射失败把功能整个关掉。
	/// </summary>
	/// <summary>★ 性能：`IsGameRunning` 的反射结果缓存（方法信息 + 单例引用）。</summary>
	private MethodInfo _isRunningMi;
	private object _isRunningMgr;
	private System.Func<bool> _isRunningFn;   // ★ 编译好的委托（避免每帧 MethodInfo.Invoke）

	private bool IsGameRunning()
	{
		try
		{
			// ★ 性能（2026-10-13 真机探针：本 Mod 回调 1.65ms/帧）：
			//   本方法**每帧**被调用，而它原先每帧都做一次
			//   `mgr.GetType().GetMethod("IsGameRunning", …)`（真反射方法查找）
			//   + `MethodInfo.Invoke`（真反射调用）。Mono/安卓上这两步就是 ms 级。
			//   ⇒ 单例与方法信息解析一次后缓存（单例被销毁时 IsInstanceValid 失效自动重解析）。
			if (_isRunningMi == null || _isRunningMgr == null
				|| (_isRunningMgr is GodotObject goCached && !GodotObject.IsInstanceValid(goCached)))
			{
				object mgr = GetSingleton("TowerDefenseManager");
				if (mgr == null)
				{
					return true;
				}
				MethodInfo mi = mgr.GetType().GetMethod("IsGameRunning",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (mi == null || mi.ReturnType != typeof(bool) || mi.GetParameters().Length != 0)
				{
					return true;
				}
				_isRunningMgr = mgr;
				_isRunningMi = mi;
				// ★★ 关键：把 `MethodInfo.Invoke` **编译成委托**。上一轮只缓存了"找方法"，
				//   但每帧仍然走 `Invoke`（Mono 上真反射调用，装箱 + 参数检查）。
				//   委托调用是纳秒级。失败则退回反射（行为不变）。
				try
				{
					_isRunningFn = (System.Func<bool>)System.Delegate.CreateDelegate(
						typeof(System.Func<bool>), mgr, mi);
				}
				catch { _isRunningFn = null; }
			}
			if (_isRunningFn != null)
			{
				return _isRunningFn();
			}
			{
				object r = _isRunningMi.Invoke(_isRunningMgr, null);
				return !(r is bool b) || b;
			}
		}
		catch { }
		return true;
	}

	/// <summary>收起原有复选框（切到样式 1/2/3 时）。</summary>
	private void HideCheckBox()
	{
		try
		{
			if (_box != null && GodotObject.IsInstanceValid(_box))
			{
				_box.Visible = false;
			}
		}
		catch { }
	}

	/// <summary>创建（或找回）自建三档选择器。父节点与「加速」相同，保证同一套主题/变换。</summary>
	private void EnsureSelector(CheckBox vanilla)
	{
		try
		{
			if (_sel != null && GodotObject.IsInstanceValid(_sel))
			{
				if (_cells.Count == 0)
				{
					CollectCells();
				}
				return;
			}
			Node parent = vanilla.GetParent();
			if (parent == null || !GodotObject.IsInstanceValid(parent))
			{
				return;
			}
			// 场景重建后可能已有一个 ⇒ 先找回，避免叠加
			Node existing = FindNodeByName(parent, SelName);
			if (existing is Control found && GodotObject.IsInstanceValid(found))
			{
				_sel = found;
				CollectCells();
				return;
			}

			EnsureCellStyles();

			PanelContainer root = new PanelContainer();
			root.Name = SelName;
			root.MouseFilter = Control.MouseFilterEnum.Stop;
			EnsureArt();
			if (ArtOn && _texArrow[0] != null)
			{
				// 外框用**整块底图**：切换档位时整张换（SyncSelector 每帧刷新）
				_sbFrameArt = MakeArtBox(_texArrow[0]);
				root.AddThemeStyleboxOverride("panel", _sbFrameArt);
			}
			else
			{
				// 代码绘制：深色底 —— 格子之间的间距透出它，形成分格线
				_sbFrameFlat = new StyleBoxFlat();
				_sbFrameFlat.BgColor = FrameBg;
				_sbFrameFlat.BorderColor = FrameLine;
				_sbFrameFlat.SetBorderWidthAll(2);
				_sbFrameFlat.SetCornerRadiusAll((int)FrameRadius);
				_sbFrameFlat.ContentMarginLeft = FramePad;
				_sbFrameFlat.ContentMarginRight = FramePad;
				_sbFrameFlat.ContentMarginTop = FramePad;
				_sbFrameFlat.ContentMarginBottom = FramePad;
				root.AddThemeStyleboxOverride("panel", _sbFrameFlat);
			}

			_cells.Clear();
			if (Style == BtnStyle.SingleGear)
			{
				// 样式 3：只有一格，显示当前档位的箭头
				Button one = MakeCellButton(0);
				one.Name = "Cell0";
				one.CustomMinimumSize = new Vector2(CellW * 1.45f, CellH);
				one.Pressed += () => OnCellClicked(0);
				root.AddChild(one);
				_cells.Add(one);
			}
			else
			{
				BoxContainer box;
				if (Style == BtnStyle.TriVertical)
				{
					VBoxContainer v = new VBoxContainer();
					v.Alignment = BoxContainer.AlignmentMode.Center;
					box = v;
				}
				else
				{
					HBoxContainer h = new HBoxContainer();
					h.Alignment = BoxContainer.AlignmentMode.Center;
					box = h;
				}
				box.AddThemeConstantOverride("separation", (int)CellGap);
				root.AddChild(box);
				for (int i = 0; i < 3; i++)
				{
					Button b = MakeCellButton(i);
					b.Name = "Cell" + i;
					b.CustomMinimumSize = new Vector2(
						CellW, (Style == BtnStyle.TriVertical) ? CellHVertical : CellH);
					// 素材模式下让格子铺满内容区 —— 否则按钮只有最小尺寸、点击区落不到素材格子上
					if (UseArt)
					{
						b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
						b.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
					}
					int idx = i;
					b.Pressed += () => OnCellClicked(idx);
					box.AddChild(b);
					_cells.Add(b);
				}
			}

			root.Visible = false;    // 等 SyncSelector 定好位再显示，避免闪一下
			parent.AddChild(root);
			_sel = root;
			if (!_selBuilt)
			{
				_selBuilt = true;
				Info("已创建加速档位选择器（样式 " + (int)Style + "，格子数=" + _cells.Count + "）。");
			}
		}
		catch (Exception ex)
		{
			Warn("创建档位选择器失败：" + ex.Message);
		}
	}

	/// <summary>
	/// 量一段文字在**基准字号**（`CellFontSize`）下的像素宽 —— 用游戏自己的主题字体，所以是准的。
	///
	/// ⚠️⚠️ 必须**固定用基准字号**量，**绝不能**用 `GetThemeFontSize()` 取当前字号：
	///   本方法的结果被用来"决定该格要不要缩小字号"，若拿当前字号量，就会形成
	///     16 号 → 判定装不下 → 改 12 号 → 下次按 12 号量 → 判定装得下 → 改回 16 号 → …
	///   的**每帧震荡**（用户实测反馈："不停的抽动"）。
	///   固定基准字号后，判定结果与"当前字号"无关 ⇒ 一次定下来就不再变。
	///
	/// 取不到字体返回 0 ⇒ 调用处退回保守的 `CellW`。
	/// </summary>
	private static float CellTextWidth(Button b, string text)
	{
		try
		{
			if (b == null || !GodotObject.IsInstanceValid(b) || string.IsNullOrEmpty(text))
			{
				return 0f;
			}
			Font f = b.GetThemeFont("font");
			if (f != null)
			{
				return f.GetStringSize(text, HorizontalAlignment.Left, -1f, CellFontSize).X;
			}
		}
		catch { }
		return 0f;
	}

	/// <summary>造一个格子按钮（外观每帧由 ApplyCellStyle 覆盖）。</summary>
	private static Button MakeCellButton(int i)
	{
		Button b = new Button();
		b.Text = GearArrow[i];
		b.FocusMode = Control.FocusModeEnum.None;
		b.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
		b.AddThemeFontSizeOverride("font_size", CellFontSize);
		b.SetMeta("ds_fs", CellFontSize);
		// 白字要配黑描边才在绿/橙/红三色底上都清楚（描边色见 ApplyCellStyle 的 font_outline_color）
		b.AddThemeConstantOverride("outline_size", 3);
		return b;
	}

	/// <summary>把选择器里的格子按钮按名字收集回来（场景重建后复用旧节点时用）。</summary>
	private void CollectCells()
	{
		try
		{
			_cells.Clear();
			for (int i = 0; i < 3; i++)
			{
				Node n = FindNodeByName(_sel, "Cell" + i);
				if (n is Button b && GodotObject.IsInstanceValid(b))
				{
					int idx = i;
					if (!b.HasMeta("ds_wired"))
					{
						b.SetMeta("ds_wired", true);
						b.Pressed += () => OnCellClicked(idx);
					}
					_cells.Add(b);
				}
			}
		}
		catch { }
	}

	/// <summary>素材文件 → 贴图（失败返回 null）。从**包解压根目录**读文件，避开 res:// 映射问题。</summary>
	private Texture2D LoadArt(string file)
	{
		try
		{
			string root = (_context == null) ? null : _context.PackageRoot;
			if (string.IsNullOrEmpty(root))
			{
				return null;
			}
			string path = System.IO.Path.Combine(root, "Resources", file);
			if (!System.IO.File.Exists(path))
			{
				return null;
			}
			Image img = Image.LoadFromFile(path);
			if (img == null)
			{
				return null;
			}
			return ImageTexture.CreateFromImage(img);
		}
		catch { return null; }
	}

	/// <summary>一次性把 10 张素材读进来；任何一张缺失就整体退回代码绘制。</summary>
	/// <summary>
	/// 从「Mod 配置」读样式（0/1/2/3 → 三格横排 / 三档竖排 / 单格循环 / 复选框）。
	/// applyMode = restart ⇒ 改完重启游戏生效（切换样式要重建控件与素材，这样最稳）。
	/// </summary>
	/// <summary>
	/// 读位置设置：auto = 沿用自动布局（右对齐菜单按钮 / 加速下方）；custom = 用绝对坐标（像素）。
	/// immediate ⇒ 面板改完立即生效。
	/// </summary>
	/// <summary>★ 需求 4+6：位置编辑器守候节流帧号。</summary>
	private static ulong _posEditorFrame;
	/// <summary>★ 样式重建后重试帧数（素材异步加载完成前重建会退化成代码绘制）。</summary>
	private static int _styleRebuildRetry;

	/// <summary>
	/// ★ 需求 4+6：在战斗设置页放「三倍加速 · 位置」编辑器（共享组件 PosEditorRow）。
	/// 编辑的是**战斗 HUD 里那个加速控件**（勾选框或档位选择器）的位置。
	/// </summary>
	private void EnsurePosEditor(Node optionRoot)
	{
		try
		{
			float bw = 102f, bh = 64f;
			try
			{
				Control c = null;
				if (_sel != null && GodotObject.IsInstanceValid(_sel)) { c = _sel; }
				else if (_box != null && GodotObject.IsInstanceValid(_box)) { c = _box; }
				if (c != null)
				{
					if (c.Size.X > 1f) { bw = c.Size.X; }
					if (c.Size.Y > 1f) { bh = c.Size.Y; }
				}
			}
			catch { }
			// ★ 2026-10-13（用户要求）：样式也能在战斗设置页里改，与配置页双向同步
			PosEditorRow.EnsureStyleRow(optionRoot, "dststyle", "加速样式",
				new string[] { "1", "2", "3", "4" },
				new string[] { "1 · 三格横排", "2 · 三档竖排", "3 · 单格循环", "4 · 原有复选框" },
				ReadStyleSetting(),
				ApplyStyleFromEditor);
			PosEditorRow.Ensure(optionRoot, "dst", "三倍加速 · 位置",
				new string[] { "auto", "custom" },
				new string[] { "自动（对齐菜单按钮）", "自定义绝对坐标" },
				"custom", ReadPosPreset(), _posX, _posY, bw, bh, ApplyPosFromEditor);
		}
		catch { }
	}

	/// <summary>读当前 positionMode（兼容 string / JsonElement；取不到按内存值推）。</summary>
	private string ReadPosPreset()
	{
		try
		{
			object v = (_context == null) ? null : _context.Settings.Get<object>(KPositionMode);
			string s = null;
			if (v is string str) { s = str; }
			else if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
			{
				s = je.GetString();
			}
			if (!string.IsNullOrWhiteSpace(s)) { return s.Trim(); }
		}
		catch { }
		return _posCustom ? "custom" : "auto";
	}

	/// <summary>★ 编辑器回调：写回配置 + 立即套用（SyncBox/SyncSelector 下一帧就会摆到位）。</summary>
	private void ApplyPosFromEditor(string preset, int x, int y)
	{
		try
		{
			_posCustom = (preset == "custom");
			_posX = x;
			_posY = y;
			try
			{
				var changes = new System.Collections.Generic.Dictionary<string, object>();
				changes[KPositionMode] = preset;
				changes[KCustomX] = x;
				changes[KCustomY] = y;
				_context?.Settings?.TrySave(changes, out XWModSettingsSaveResult _);
			}
			catch { }
			ApplyPositionSetting();
		}
		catch { }
	}


	/// <summary>读了当前样式值（供设置页样式行回填）。</summary>

	/// <summary>★ 2026-10-13：取可视视口尺寸（百分比坐标换算用；取不到按设计分辨率）。</summary>
	private Vector2 GetViewportSizeForPos()
	{
		try
		{
			Node rt = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (rt != null)
			{
				Vector2 v = rt.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
				if (v.X > 100f && v.Y > 100f) { return v; }
			}
		}
		catch { }
		return new Vector2(1080f, 600f);
	}

	private string ReadStyleSetting()
	{
		try
		{
			object v = (_context == null) ? null : _context.Settings.Get<object>(KStyle);
			if (v is string s) { return s; }
			if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String) { return je.GetString(); }
		}
		catch { }
		return "1";
	}

	/// <summary>
	/// ★ 设置页样式行回调：写回配置 + **立即重建战斗 HUD 控件**（与配置页双向同步）。
	///
	/// 实测"修改样式无效"的原因：战斗 HUD 里那个三格/竖排/单格控件是**开战时按当时的 Style 建好**的，
	/// 本 Mod 原有的热更新路径只重画配置页的演示图，不会重建战场控件 ⇒ 改完样式要重开一局才看到。
	/// 这里把旧控件 QueueFree 掉，交给下面每帧跑的 `SyncBox` / `SyncSelector` 按新 Style 重建（它们本来就会"没有就建"）。
	/// </summary>
	private void ApplyStyleFromEditor(string value)
	{
		try
		{
			try
			{
				var ch = new System.Collections.Generic.Dictionary<string, object>();
				ch[KStyle] = value;
				bool _ok = _context?.Settings?.TrySave(ch, out XWModSettingsSaveResult _res) ?? false;
				try { Diag("样式写配置 ok=" + _ok + " value=" + value); } catch { }
			}
			catch { }
			// ★★ 2026-10-13（用户实测"改了样式还是没效果"）：**不再只依赖配置往返**——
			//   宿主把新值推回实时快照这条链有一次没走通就全盘无效，所以这里**直接把字段改掉**，
			//   再做与该 Mod 自己的热更新完全相同的操作（允许重建 + 释放旧控件）⇒ 下一帧必重建。
			try
			{
				ApplyStyleSetting();   // ★ 先按配置对齐（它可能读到旧值，下面再直接写一次覆盖它）
				BtnStyle want = Style;
				if (value == "1" || value.Equals("tri", StringComparison.OrdinalIgnoreCase)) { want = BtnStyle.TriHorizontal; }
				else if (value == "2" || value.Equals("vert", StringComparison.OrdinalIgnoreCase)) { want = BtnStyle.TriVertical; }
				else if (value == "3" || value.Equals("single", StringComparison.OrdinalIgnoreCase)) { want = BtnStyle.SingleGear; }
				else if (value == "4" || value.Equals("checkbox", StringComparison.OrdinalIgnoreCase)) { want = BtnStyle.CheckBox; }
				Style = want;
				_selBuilt = false;
				try { HideSelector(); } catch { }
				if (_sel != null && GodotObject.IsInstanceValid(_sel)) { _sel.QueueFree(); }
				_sel = null;
				try { _cells.Clear(); } catch { }
				if (_box != null && GodotObject.IsInstanceValid(_box)) { _box.QueueFree(); _box = null; }
				Diag("样式已由设置页改为：" + Style + "（战场控件下一帧重建）");
			}
			catch { }
			// ★★ 2026-10-13（日志实证：改完样式 `sel=null` ⇒ 旧控件被释放后再没重建）：
			//   **直接调用该 Mod 自己的创建/布局路径重建一次**，不再干等每帧那条链
			//   （它每场战斗只建一次的风格，等不到就会一直是空的）。
			try
			{
				CheckBox van = VanillaBoxCached();
				// ★★ 2026-10-13（用户实测："1·三格横排 怎么变成代码绘制的箭头了、原来有素材"）：
				//   直接在回调里重建时会**赶在素材加载完成之前**（`_artOk` 还是 false）⇒ 走成代码绘制，
				//   而 `_selBuilt` 已置位 ⇒ 每帧路径认为"建过了"，素材就再也不出现了 ✗。
				//   先 `EnsureArt()` 强制加载素材，再重建；并登记重试几帧兜底。
				try { EnsureArt(); } catch { }
				EnsureSelector(van);
				SyncSelector(van);
				_styleRebuildRetry = 3;
				Diag("样式重建结果：sel=" + ((_sel == null) ? "null(未建)" : "ok")
					+ " cells=" + _cells.Count + " 样式=" + (int)Style);
			}
			catch (Exception ex)
			{
				try { Diag("样式重建异常：" + ex.Message); } catch { }
			}
			// ★ 这里**不能再调 ApplyStyleSetting()**：它会用（可能未更新的）配置值覆盖我的直接写入 ✗
			//   —— 这正是"切换加速样式还是不行"的原因。
		}
		catch { }
	}

	private void ApplyPositionSetting()
	{
		try
		{
			object v = (_context == null) ? null : _context.Settings.Get<object>(KPositionMode);
			string s = null;
			if (v is string str) { s = str; }
			else if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.String) { s = je.GetString(); }
				else if (je.ValueKind == System.Text.Json.JsonValueKind.Number) { s = je.ToString(); }
			}
			if (!string.IsNullOrWhiteSpace(s))
			{
				s = s.Trim();
				_posCustom = s.Equals("custom", StringComparison.OrdinalIgnoreCase) || s == "2";
			}
			_posX = ReadIntSetting(KCustomX, _posX);
			_posY = ReadIntSetting(KCustomY, _posY);
		}
		catch { }
	}

	/// <summary>读整数配置（兼容 long / JsonElement / string）。</summary>
	private int ReadIntSetting(string key, int fallback)
	{
		try
		{
			object v = (_context == null) ? null : _context.Settings.Get<object>(key);
			if (v == null) { return fallback; }
			if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out int n)) { return n; }
				if (je.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(je.GetString(), out int ns)) { return ns; }
				return fallback;
			}
			return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
		}
		catch { return fallback; }
	}

	private void ApplyStyleSetting()
	{
		try
		{
			object v = (_context == null) ? null : _context.Settings.Get<object>(KStyle);
			string s = null;
			if (v is string str) { s = str; }
			else if (v is System.Text.Json.JsonElement je)
			{
				if (je.ValueKind == System.Text.Json.JsonValueKind.String) { s = je.GetString(); }
				else if (je.ValueKind == System.Text.Json.JsonValueKind.Number) { s = je.ToString(); }
			}
			if (string.IsNullOrWhiteSpace(s)) { return; }
			s = s.Trim();
			if (s == "1" || s.Equals("tri", StringComparison.OrdinalIgnoreCase)) { Style = BtnStyle.TriHorizontal; }
			else if (s == "2" || s.Equals("vert", StringComparison.OrdinalIgnoreCase)) { Style = BtnStyle.TriVertical; }
			else if (s == "3" || s.Equals("single", StringComparison.OrdinalIgnoreCase)) { Style = BtnStyle.SingleGear; }
			else if (s == "4" || s.Equals("checkbox", StringComparison.OrdinalIgnoreCase)) { Style = BtnStyle.CheckBox; }
		}
		catch { }
	}

	/// <summary>★ 2026-10-13：已加载素材对应的样式（素材按样式分三套，样式变了必须重载）。</summary>
	private BtnStyle _artStyleLoaded;
	private bool _artStyleInit;

	private void EnsureArt()
	{
		// ★★ 2026-10-13（用户实测："2 和 3 直接不显示了"）：
		//   素材是**按样式分三套**的（style1_gear*=三格横排 / style2_gear*=三档竖排 / style3_gear*=单格循环），
		//   而这里原来只 `if (_artTried) return;` —— **只在第一次按当时的样式加载一套，永不重载** ✗。
		//   于是从 1 切到 2/3 时，仍拿"三格横排"那张图去画竖排/单格 ⇒ 尺寸对不上 ⇒ 控件直接不显示 ✗。
		//   现在：**记住已加载素材属于哪个样式，样式一变就重新加载对应那套**。
		if (_artTried && _artStyleInit && _artStyleLoaded == Style)
		{
			return;
		}
		_artTried = true;
		_artStyleInit = true;
		_artStyleLoaded = Style;
		_artOk = false;
		_texArrow[0] = null; _texArrow[1] = null; _texArrow[2] = null;
		_sbFrameArt = null;
		try
		{
			if (!UseArt)
			{
				return;
			}
			// 整块烘焙图：外框 + 三格(或单格) + 箭头都在一张里，每档一张
			string prefix = (Style == BtnStyle.SingleGear) ? "style3_gear"
				: ((Style == BtnStyle.TriVertical) ? "style2_gear" : "style1_gear");
			bool ok = true;
			for (int i = 0; i < 3; i++)
			{
				_texArrow[i] = LoadArt(prefix + (i + 1) + ".png");
				ok = ok && _texArrow[i] != null;
			}
			_artOk = ok;
			if (ok && _texArrow[0] != null)
			{
				_artW = _texArrow[0].GetWidth();
				_artH = _texArrow[0].GetHeight();
			}
			Info(ok ? ("素材已加载（贴图模式，" + _artW.ToString("0") + "x" + _artH.ToString("0") + "）。")
				: "素材不完整 ⇒ 退回代码绘制。");
		}
		catch (Exception ex)
		{
			_artOk = false;
			Warn("加载素材失败（退回代码绘制）：" + ex.Message);
		}
	}

	/// <summary>两个复用的 StyleBox（避免每帧 new）。</summary>
	private void EnsureCellStyles()
	{
		try
		{
			EnsureArt();
			if (_sbGear != null && _sbGearDim != null)
			{
				return;
			}
			if (ArtOn)
			{
				// 素材模式：**整块底图**已经画好了外框/格子/箭头 ⇒
				//   格子按钮只需要"能被点到"，外观必须完全透明，别盖住底图。
				_sbGear = new StyleBox[3];
				_sbGearDim = new StyleBox[3];
				for (int i = 0; i < 3; i++)
				{
					_sbGear[i] = new StyleBoxEmpty();
					_sbGearDim[i] = new StyleBoxEmpty();
				}
				return;
			}
			_sbGear = new StyleBox[3];
			_sbGearDim = new StyleBox[3];
			for (int i = 0; i < 3; i++)
			{
				// 注意：数组类型是 StyleBox[]（要能放 StyleBoxTexture），
				// 所以这里必须用局部变量 BuildStyleBoxFlat 后再赋进去。
				StyleBoxFlat on = new StyleBoxFlat();
				on.BgColor = GearBg[i];
				on.SetBorderWidthAll(0);
				on.SetCornerRadiusAll(9);
				on.ContentMarginLeft = 2f;
				on.ContentMarginRight = 2f;
				on.ContentMarginTop = 1f;
				on.ContentMarginBottom = 1f;
				_sbGear[i] = on;

				StyleBoxFlat off = new StyleBoxFlat();
				off.BgColor = GearBgDim[i];
				off.SetBorderWidthAll(0);
				off.SetCornerRadiusAll(9);
				off.ContentMarginLeft = 2f;
				off.ContentMarginRight = 2f;
				off.ContentMarginTop = 1f;
				off.ContentMarginBottom = 1f;
				_sbGearDim[i] = off;
			}
		}
		catch { }
	}

	/// <summary>把一张 3 倍九宫贴图包成 StyleBoxTexture。</summary>
	private static StyleBoxTexture MakeArtBox(Texture2D tex)
	{
		StyleBoxTexture sb = new StyleBoxTexture();
		sb.Texture = tex;
		// ★ 素材是 **1× 图**（110x36 / 105x36），所以九宫边距也必须按 1× 给：
		//   左右 18（= 胶囊半径 18px），上下 0。
		//   ⚠️ 踩过的坑：边距是**纹理像素**、不随缩放变。之前素材是 3 倍图、边距写 54，
		//      结果 110px 宽的控件被两端吃掉 54+54=108，中间那格只剩 2px ⇒ 显示成绿|红两段。
		sb.TextureMarginLeft = 18;
		sb.TextureMarginRight = 18;
		sb.TextureMarginTop = 0;
		sb.TextureMarginBottom = 0;
		sb.ContentMarginLeft = 0f;
		sb.ContentMarginRight = 0f;
		sb.ContentMarginTop = 0f;
		sb.ContentMarginBottom = 0f;
		return sb;
	}

	/// <summary>给一个格子上色（选中 = 红底白字，未选中 = 橙底近黑字）。</summary>
	private void ApplyCellStyle(Button b, bool on, int gear)
	{
		try
		{
			EnsureCellStyles();
			if (gear < 0) { gear = 0; }
			if (gear > 2) { gear = 2; }
			StyleBox sb = on ? _sbGear[gear] : _sbGearDim[gear];
			b.AddThemeStyleboxOverride("normal", sb);
			b.AddThemeStyleboxOverride("hover", sb);
			b.AddThemeStyleboxOverride("pressed", sb);
			b.AddThemeColorOverride("font_color", CellFg);
			b.AddThemeColorOverride("font_hover_color", CellFg);
			b.AddThemeColorOverride("font_pressed_color", CellFg);
			b.AddThemeColorOverride("font_focus_color", CellFg);
			b.AddThemeColorOverride("font_outline_color", CellFgOutline);
		}
		catch { }
	}

	/// <summary>只刷新格子外观/文字（不重新定位）。</summary>
	private void SyncCells()
	{
		try
		{
			bool single = (_cells.Count == 1);
			for (int i = 0; i < _cells.Count; i++)
			{
				Button b = _cells[i];
				if (b == null || !GodotObject.IsInstanceValid(b))
				{
					continue;
				}
				// 单格样式：这一格永远代表**当前档**；三格样式：第 i 格代表第 i 档
				int gear = single ? _gear : i;
				bool lit = single || (i == _gear);

				if (ArtOn)
				{
					// 素材模式：箭头已烘焙进底图 ⇒ 按钮上不要任何文字/图标
					if (!string.IsNullOrEmpty(b.Text))
					{
						b.Text = "";
					}
					if (b.Icon != null)
					{
						b.Icon = null;
					}
				}
				else
				{
					// 代码绘制：文字箭头
					if (b.Icon != null)
					{
						b.Icon = null;
					}
					string want = GearArrow[gear];
					if (b.Text != want)
					{
						b.Text = want;
					}
				}
				ApplyCellStyle(b, lit, gear);
			}
		}
		catch { }
	}

	/// <summary>
	/// 每帧把选择器摆到「加速」原来的位置（右对齐、顶边对齐），并刷新配色。
	/// 样式 1/2/3 下会**隐藏游戏自带复选框** —— 三档里已含"原版加速"，两套并存会重复。
	/// </summary>
	private void SyncSelector(CheckBox vanilla)
	{
		try
		{
			Control sel = _sel;
			if (sel == null || !GodotObject.IsInstanceValid(sel))
			{
				return;
			}
			// 记下"我们动手之前"它是不是可见的，未开始时好还原
			if (!_hidVanilla)
			{
				_vanillaPrevVisible = vanilla.Visible;
				_hidVanilla = true;
			}
			if (vanilla.Visible)
			{
				vanilla.Visible = false;
			}
			if (!sel.Visible)
			{
				sel.Visible = true;
			}
			if (_cells.Count == 0)
			{
				CollectCells();
			}
			SyncCells();

			// ★ 素材模式：底图按**当前档位**整张换（3 张预烘焙图）
			if (ArtOn && _sbFrameArt != null && _texArrow[_gear] != null
				&& _sbFrameArt.Texture != _texArrow[_gear])
			{
				_sbFrameArt.Texture = _texArrow[_gear];
			}

			// ★★ 先找「菜单」按钮（`GUITop/ButtonPause`）—— 它同时决定两件事：
			//     ① 定位：整个控件**右对齐**它的右边缘；
			//     ② 尺寸：单格样式（样式 3）要**与它等长**。
			//   时停也是锚在同一个右边缘、同样从菜单下方起算 ⇒ 两者天然等长。
			Control pauseBtn = FindPauseButton(vanilla);
			float pauseW = 0f;        // 菜单按钮的**视觉**宽度（含 scale）
			float pauseRightX = 0f;   // 菜单按钮的**视觉**右边缘
			if (pauseBtn != null && GodotObject.IsInstanceValid(pauseBtn) && pauseBtn.Visible)
			{
				float psx = 1f;
				try { psx = pauseBtn.Scale.X; } catch { }
				if (psx <= 0.01f) { psx = 1f; }
				pauseW = pauseBtn.Size.X * psx;
				pauseRightX = pauseBtn.GlobalPosition.X + pauseW;
			}

			// ★★ 用户要求（2026-10-09，二次更正）：**三格等宽，宽度按"一二档字符"来定**。
			//   即取 `->` / `->>` 里较宽的那个作为统一宽度（不是把一二档撑到 `->>>` 那么宽）。
			//   第三档 `->>>` 字符多一个 ⇒ **自动缩小它的字号**到刚好装进这个宽度，
			//   这样既不裁切、也不会把整个控件撑宽。
			float cellMax = CellW;
			float t1 = 0f, t2 = 0f;
			if (_cells.Count >= 2)
			{
				t1 = CellTextWidth(_cells[0], GearArrow[0]) + CellMarginX;
				t2 = CellTextWidth(_cells[1], GearArrow[1]) + CellMarginX;
				if (t1 > cellMax) { cellMax = t1; }
				if (t2 > cellMax) { cellMax = t2; }
			}
			// ★ 素材模式：格子尺寸用素材的基准值（箭头是图形，不再由文字宽度决定）；
			//   同时**不再需要缩字号**（下面那段会自动跳过）。
			if (ArtOn)
			{
				// 素材是固定尺寸的整块图（样式1 110x36 / 样式2 105x110 / 样式3 105x36）
				// ⇒ 格子尺寸按素材里实际划分反推，这样点击区正好落在格子上。
				//   外框黑边 4、格间距 = CellGap。
				float iw = _artW - 8f, ih = _artH - 8f;
				if (Style == BtnStyle.TriVertical)
				{
					cellMax = iw;
					cellMaxH = (ih - 2f * CellGap) / 3f;
				}
				else if (Style == BtnStyle.SingleGear)
				{
					cellMax = iw;
					cellMaxH = ih;
				}
				else
				{
					cellMax = (iw - 2f * CellGap) / 3f;
					cellMaxH = ih;
				}
			}
			// ★★ 单格样式（样式 3）：**与「菜单」/「时停」等长** —— 直接取菜单按钮的视觉宽，
			//   再减掉外框的内边距(2×2)与描边(2×2)，剩下的就是格子该有的宽度。
			//   （单格里最多只有 4 个字符 `->>>`，远窄于菜单按钮 ⇒ 不会触发缩字号。）
			if (Style == BtnStyle.SingleGear && pauseW > 8f)
			{
				float inner = pauseW - (FramePad * 2f + 4f);
				if (inner > cellMax)
				{
					cellMax = inner;
				}
			}
			if (!_widthReported)
			{
				_widthReported = true;
				Diag("格子宽度：-> = " + t1.ToString("0.#")
					+ "px，->> = " + t2.ToString("0.#")
					+ "px；菜单按钮宽 = " + pauseW.ToString("0.#")
					+ "px ⇒ 统一宽 = " + cellMax.ToString("0.#") + "px");
			}
			// 竖排时格子给高一点（素材模式统一用素材基准高）
			float cellH = ArtOn ? cellMaxH
				: ((Style == BtnStyle.TriVertical) ? CellHVertical : CellH);
			for (int ci = 0; ci < _cells.Count; ci++)
			{
				Button cb2 = _cells[ci];
				if (cb2 == null || !GodotObject.IsInstanceValid(cb2))
				{
					continue;
				}
				cb2.CustomMinimumSize = new Vector2(cellMax, cellH);

				// 文字装不下就把这格的字号按比例缩小（只影响装不下的那格）
				// 素材模式箭头是图形、没有文字 ⇒ 整段跳过
				if (ArtOn)
				{
					continue;
				}
				float need = CellTextWidth(cb2, cb2.Text) + CellMarginX;
				int fsWant = CellFontSize;
				if (need > cellMax && need > 0.1f)
				{
					fsWant = (int)Mathf.Floor(CellFontSize * (cellMax / need));
					if (fsWant < 9) { fsWant = 9; }
				}
				if (cb2.HasMeta("ds_fs") && cb2.GetMeta("ds_fs").AsInt32() == fsWant)
				{
					continue;
				}
				cb2.SetMeta("ds_fs", fsWant);
				cb2.AddThemeFontSizeOverride("font_size", fsWant);
			}

			// 尺寸自己算（不依赖容器的延迟布局）：格子 + 间距 + 内边距 + 2px 边框 ×2
			// ★ 竖排（样式 2）与横排的"宽高"是**对调**的 —— 原来一律按三列宽算，
			//   竖排时会撑成一个很宽的大方块（本次修掉）。
			float bw, bh;
			if (Style == BtnStyle.SingleGear)
			{
				bw = cellMax;   // ★ 单格占满控件内部宽度（cellMax 已按菜单按钮宽算好）
				bh = cellH;
			}
			else if (Style == BtnStyle.TriVertical)
			{
				bw = cellMax;                            // 一列
				bh = 3f * cellH + 2f * CellGap;          // 三行
			}
			else
			{
				bw = 3f * cellMax + 2f * CellGap;        // 三列
				bh = cellH;                              // 一行
			}
			Vector2 want = new Vector2(bw + FramePad * 2f + 4f, bh + FramePad * 2f + 4f);
			// ★ 素材模式：控件尺寸**直接取素材的 1× 尺寸** ⇒ 1:1 绘制、不变形
			//   （素材本身就是按这个尺寸设计的，横竖样式各自一套）
			if (ArtOn && _artW > 8f && _artH > 8f)
			{
				want = new Vector2(_artW, _artH);
			}

			float vScaleX = 1f;
			try
			{
				vScaleX = vanilla.Scale.X;
			}
			catch { }
			if (vScaleX <= 0.01f) { vScaleX = 1f; }

			Vector2 vSize = vanilla.Size;
			Vector2 vGlobal = vanilla.GlobalPosition;
			float vRightX = vGlobal.X + vSize.X * vScaleX;

			// 右边缘：★ 用户要求 —— **右对齐到「菜单」按钮**（拿不到才退回对齐「加速」）
			//   菜单按钮的视觉右边缘已在前面算好（`pauseRightX`）。
			float rightX = (pauseRightX > 0.5f) ? pauseRightX : vRightX;
			// 位置设置：自定义坐标时直接用绝对坐标，不走自动布局
			float myLeftG, myTopG;
			if (_posCustom)
			{
				// ★★ 2026-10-13（用户实测"加速无效、X 到 91/92 就顶"）：**这里之前还在按像素用** ✗
				//   （上一轮我只改了时停的套用处）⇒ customX/customY 现在是 0~100 百分比，按视口换算。
				Vector2 vpPct = GetViewportSizeForPos();
				myLeftG = vpPct.X * Mathf.Clamp(_posX, 0, 100) / 100f;
				myTopG = vpPct.Y * Mathf.Clamp(_posY, 0, 100) / 100f;
			}
			else
			{
				myLeftG = rightX - want.X;
				myTopG = vGlobal.Y;
			}

			sel.CustomMinimumSize = want;
			sel.Size = want;
			sel.GlobalPosition = new Vector2(myLeftG, myTopG);

			if (!_selBuilt)
			{
				_selBuilt = true;
				Diag("选择器布局：gpos=(" + myLeftG.ToString("0.#") + "," + myTopG.ToString("0.#")
					+ ") size=(" + want.X.ToString("0.#") + "," + want.Y.ToString("0.#")
					+ ") 样式=" + (int)Style);
			}
		}
		catch { }
	}

	/// <summary>点了一个格子。</summary>
	private void OnCellClicked(int idx)
	{
		try
		{
			// 样式 3 只有一格 ⇒ 单击循环；样式 1/2 三格各对应一档 ⇒ 点哪档跳哪档
			int g = (Style == BtnStyle.SingleGear) ? ((_gear + 1) % 3) : idx;
			// ★ 用户要求：点**已经选中的那一档**时什么都不做 ——
			//   尤其是**不要再放一次音效**（原来会重复触发，听起来像"点了有效果"）。
			if (g == _gear)
			{
				return;
			}
			ApplyGear(g);
		}
		catch (Exception ex)
		{
			Warn("切档异常：" + ex.Message);
		}
	}

	/// <summary>
	/// 切到指定档位（三档模型的**唯一入口**）：
	///   0 = 正常速度（`baseTimeScale`）
	///   1 = 游戏加速（`baseTimeScale × 1.5`，QUIZ 关 ×3）
	///   2 = 本 Mod 加速（在游戏加速档上再 ×`DoubleFactor`）
	/// </summary>	/// <summary>快捷键配置字符串 → Key（空 = None；单字符 ASCII；多字符按枚举名）。</summary>
	/// <summary>★ 性能：`ParseKeySetting` 是 `Enum.TryParse`（Mono 上靠反射），而它在每帧路径上
	/// 被调用；绑定串不变时结果也不会变 ⇒ 按字符串缓存。</summary>
	private string _pkFrom;
	private Key _pkCached;

	private Key ParseKeySettingCached(string s)
	{
		if (s == _pkFrom)
		{
			return _pkCached;
		}
		_pkFrom = s;
		_pkCached = ParseKeySetting(s);
		return _pkCached;
	}

	private static Key ParseKeySetting(string s)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(s)) { return Key.None; }
			s = s.Trim();
			if (s.Length == 1)
			{
				int code = s[0];
				if (code >= 32 && code <= 126) { return (Key)code; }
				return Key.None;
			}
			if (Enum.TryParse(s, true, out Key k)) { return k; }
			return Key.None;
		}
		catch { return Key.None; }
	}

	/// <summary>把录到的键写回配置（hotkeyNext），立即生效。</summary>
	private void WriteHotkeySetting(string keySetting)
	{
		try
		{
			if (_context?.Settings == null) { Warn("配置不可用，快捷键未保存。"); return; }
			var changes = new System.Collections.Generic.Dictionary<string, object>();
			changes[KHotkeyNext] = keySetting ?? "";
			if (_context.Settings.TrySave(changes, out XWModSettingsSaveResult result))
			{
				Info("快捷键已保存：" + (string.IsNullOrEmpty(keySetting) ? "（无绑定）" : keySetting) + "（" + result.Message + "）");
			}
			else
			{
				Warn("快捷键保存失败：" + result.Message);
			}
		}
		catch (Exception ex) { Warn("快捷键保存异常：" + ex.Message); }
	}

	/// <summary>读取快捷键字符（默认无绑定；enum 下拉或录制面板写回）。</summary>
	private string ReadHotkey()
	{
		try
		{
			object v = (_context == null) ? null : _context.Settings.Get<object>(KHotkeyNext);
			string s = null;
			if (v is string str) { s = str; }
			else if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String) { s = je.GetString(); }
			if (!string.IsNullOrWhiteSpace(s)) { return s.Trim(); }
		}
		catch { }
		return "";
	}

	/// <summary>
	/// 快捷键：在三个档位间循环（0 → 1 → 2 → 0），走与点格子相同的 ApplyGear。
	/// ⚠️ 无源生成器 ⇒ 不能用 _Input；独立一条 process_frame 连接，不动原有主循环。
	/// </summary>
	private void OnHotkeyTick()
	{
		try
		{
			// ★ v1.4.4：键位录制会话期间跳过（防录到的键被当快捷键执行）
			if (KeybindPanelManager.IsCapturing()) { _hotkeyPrev = false; return; }
			if (!IsGameRunning())
			{
				_hotkeyPrev = false;
				return;   // 没进游戏不响应（与显示门控一致）
			}
			string key = ReadHotkey();
			if (string.IsNullOrEmpty(key)) { return; }
			// ★ 2026-10-13 修：原 `char ch = key[0]` 对 VolumeUp/VolumeDown 这类枚举名键
			//   会退化成首字母（录"手机音量+"实际轮询字母 V）⇒ 按音量键毫无反应、
			//   只触发了系统音量。改用 ParseKeySettingCached（Enum.TryParse + 按串缓存）。
			Key hk = ParseKeySettingCached(key);
			if (hk == Key.None) { return; }
			bool down = false;
			try { down = Input.IsKeyPressed(hk); } catch { }
			if (down && !_hotkeyPrev)
			{
				int next = (_gear + 1) % 3;
				Info("快捷键 " + key + " 切换档位 → " + next);
				ApplyGear(next);
			}
			_hotkeyPrev = down;
		}
		catch { }
	}


	private void ApplyGear(int g)
	{
		try
		{
			if (g < 0) { g = 0; }
			if (g > 2) { g = 2; }
			// 已经是这一档 ⇒ 不重复施加、不放音效（与 OnCellClicked 的早退双保险）
			if (g == _gear && _gearInit)
			{
				return;
			}
			_gear = g;
			_gearInit = true;
			_on = (g == 2);

			double baseTs = BaseTimeScale();
			double mult = IsQuizLevel() ? 3.0 : 1.5;
			switch (g)
			{
				case 1:
					SetVanillaPressed(true);
					SetTimeScaleRaw(baseTs * mult);
					PlaySound("2XSpeedOn");
					break;
				case 2:
					SetVanillaPressed(false);
					SetTimeScaleRaw(baseTs * mult * DoubleFactor);
					PlaySound("2XSpeedOn");
					break;
				default:
					SetVanillaPressed(false);
					SetTimeScaleRaw(baseTs);
					PlaySound("2XSpeedDown");
					break;
			}
			// 复选框（样式 4）与选择器（样式 1/2/3）都要跟上
			if (_box != null && GodotObject.IsInstanceValid(_box) && _box.ButtonPressed != _on)
			{
				_suppress = true;
				_box.ButtonPressed = _on;
			}
			SyncCells();
			Info("档位 → " + g + "（" + GearName[g] + "）：TimeScale=" + CurrentTimeScaleText());
		}
		catch (Exception ex)
		{
			Warn("切档失败：" + ex.Message);
		}
		finally { _suppress = false; }
	}

	private void PlaySound(string name)
	{
		try
		{
			object am = GetSingleton("AudioManager");
			if (am == null)
			{
				return;
			}
			MethodInfo m = null;
			foreach (MethodInfo mi in am.GetType().GetMethods(
				BindingFlags.Public | BindingFlags.Instance))
			{
				if (mi.Name == "AudioPlay" && mi.GetParameters().Length >= 1)
				{
					m = mi;
					break;
				}
			}
			if (m == null)
			{
				return;
			}
			ParameterInfo[] ps = m.GetParameters();
			object[] args = new object[ps.Length];
			for (int i = 0; i < ps.Length; i++)
			{
				Type t = ps[i].ParameterType;
				if (i == 0)
				{
					args[i] = name;
				}
				else if (t == typeof(bool))
				{
					args[i] = false;
				}
				else if (t == typeof(int))
				{
					args[i] = 0;
				}
				else if (t == typeof(float))
				{
					args[i] = 0f;
				}
				else if (t == typeof(double))
				{
					args[i] = 0.0;
				}
				else if (t.IsEnum)
				{
					// 第二个参数是 AudioManagerEnum.TYPE；取第一个枚举值兜底
					Array vals = Enum.GetValues(t);
					args[i] = (vals.Length > 0) ? vals.GetValue(0) : Activator.CreateInstance(t);
				}
				else if (t.IsValueType)
				{
					args[i] = Activator.CreateInstance(t);
				}
				else
				{
					args[i] = null;
				}
			}
			m.Invoke(am, args);
		}
		catch { }
	}

	// ================================================================ 反射/查找小工具

	/// <summary>
	/// 取游戏单例。`Instance` 在游戏源码里**不一致**：
	/// `TowerDefenseManager.Instance` / `Global.Instance` 是**属性**，
	/// 而 `GameSaveManager.Instance` 是**字段** ⇒ 两种都要试。
	/// </summary>
	private object GetSingleton(string typeName)
	{
		try
		{
			Type t;
			if (!_typeCache.TryGetValue(typeName, out t))
			{
				t = null;
				foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					try { t = asm.GetType(typeName, throwOnError: false); } catch { }
					if (t != null)
					{
						break;
					}
				}
				if (t != null)
				{
					_typeCache[typeName] = t;      // 只缓存成功结果
				}
			}
			if (t == null)
			{
				return null;
			}
			PropertyInfo p = t.GetProperty("Instance",
				BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
			if (p != null)
			{
				return p.GetValue(null);
			}
			FieldInfo f = t.GetField("Instance",
				BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
			if (f != null)
			{
				return f.GetValue(null);
			}
		}
		catch { }
		return null;
	}

	/// <summary>
	/// 把倍率格式化成勾选框文本（v1.0.3）。
	///   整数 ⇒ 不带小数点：`3` → `"3x"`、`2` → `"2x"`、`6` → `"6x"`
	///   非整数 ⇒ 保留有效小数：`1.5` → `"1.5x"`、`2.25` → `"2.25x"`
	/// ⚠️ 不要用 `Convert.ToString(double)` —— 游戏附带的 .NET 是裁剪过的，
	///   本项目已实测 `Convert.ToString(object)` 会抛 `Method not found`。`double.ToString` 是安全的。
	/// </summary>
	private static string FmtX(double v)
	{
		try
		{
			double r = Math.Round(v, 2);
			double i = Math.Round(r);
			if (Math.Abs(r - i) < 0.001)
			{
				return ((long)i).ToString() + "x";
			}
			return r.ToString("0.##") + "x";
		}
		catch { return v.ToString("0.##") + "x"; }
	}

	private static Type FindType(string simpleName)
	{
		try
		{
			foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					Type t = asm.GetType(simpleName, throwOnError: false);
					if (t != null)
					{
						return t;
					}
				}
				catch { }
			}
		}
		catch { }
		return null;
	}

	/// <summary>读成员（属性优先，其次字段），支持静态与实例。</summary>
	private static object GetMember(object obj, string name)
	{
		if (obj == null || string.IsNullOrEmpty(name))
		{
			return null;
		}
		try
		{
			Type t = obj.GetType();
			PropertyInfo p = t.GetProperty(name,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			if (p != null)
			{
				return p.GetValue(p.GetGetMethod(true) != null && p.GetGetMethod(true).IsStatic ? null : obj);
			}
			FieldInfo f = t.GetField(name,
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
			if (f != null)
			{
				return f.GetValue(f.IsStatic ? null : obj);
			}
		}
		catch { }
		return null;
	}

	private static Node FindNodeByClassName(Node node, string className)
	{
		if (node == null)
		{
			return null;
		}
		try
		{
			// 注意：C# 侧类名是 `TowerDefenseControlNew`，匹配用"包含"更稳
			string tn = node.GetType().Name;
			if (tn == className || tn.StartsWith(className, StringComparison.Ordinal))
			{
				return node;
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node r = FindNodeByClassName(node.GetChild(i), className);
				if (r != null)
				{
					return r;
				}
			}
		}
		catch { }
		return null;
	}

	/// <summary>
	/// 按节点名递归找。
	/// ⚠️ 名字比较必须 `node.Name.ToString() == name` —— `Node.Name` 是 **`StringName`**，
	///   直接与 `string` 比在实机上不生效（本项目踩过：日志恒"找到=0/7"）。
	/// </summary>
	private static Node FindNodeByName(Node node, string name)
	{
		if (node == null)
		{
			return null;
		}
		try
		{
			if (node.Name.ToString() == name)
			{
				return node;
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				Node r = FindNodeByName(node.GetChild(i), name);
				if (r != null)
				{
					return r;
				}
			}
		}
		catch { }
		return null;
	}

	// ================================================================ 日志

	/// <summary>诊断直出（受 <see cref="EnableDiagLog"/> 门控）。方法体内必须是 GD.Print！</summary>
	private void Diag(string msg)
	{
		if (!EnableDiagLog)
		{
			return;
		}
		try { GD.Print(LogPrefix + msg); } catch { }
	}

	private void Info(string msg)
	{
		// ★ 2026-10-14：原先绕过日志开关，现统一受「输出诊断日志」(EnableDiagLog) 控制
		if (!EnableDiagLog)
		{
			return;
		}
		try
		{
			if (_context != null) { _context.Log(LogPrefix + msg); }
			else { GD.Print(LogPrefix + msg); }
		}
		catch { }
	}

	private void Warn(string msg)
	{
		if (!EnableDiagLog) { return; }
		// ★ 2026-10-14：该输出原先绕过日志开关，现统一受 enableLog 控制
		try
		{
			if (_context != null) { _context.Warn(LogPrefix + msg); }
			else { GD.PrintErr(LogPrefix + msg); }
		}
		catch { }
	}
}
