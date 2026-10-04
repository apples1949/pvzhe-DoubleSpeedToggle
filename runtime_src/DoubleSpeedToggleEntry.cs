// ============================================================================
//  「双倍加速」Mod —— 在游戏自带「加速」复选框的**左侧**新增一个勾选框。
//
//  行为（用户需求 2026-10-01）：
//    · 勾选本勾选框 ⇒ 在**原有加速的基础上再双倍**（= 原有加速速度 ×2）
//    · 同时**取消原有的加速**（两者互斥，绝不会叠加成两层）
//    · 再次点击本勾选框取消 ⇒ 回到普通速度
//    · 取消勾选原加速时，本勾选框也同步取消（不会残留"只勾了新框"的错觉）
//    · 位置：原「加速」左侧，**不与右下角的「时停」按钮冲突**
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
//   本 Mod 挂的是**右上角「加速」的左侧**（`CheckBox2X`，GUITop 下，
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
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

public sealed class DoubleSpeedToggleEntry : IXWModRuntimeEntry
{
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
	private const float VGapPx = 6f;

	/// <summary>
	/// 勾选框**初始**文本（v1.0.3）。
	/// ⚠️ 界面上显示的**真实倍率**由 `SyncBox()` 按**关卡实际 `baseTimeScale`** 算出后覆盖：
	///   普通关 → `3x`；`baseTimeScale = 2.0` 的 14 个关卡 → `6x`；QUIZ 关 → `6x`。
	///   这里只是"还没算出来之前"的占位（普通关的常见值），第 1 帧就会被覆盖。
	/// </summary>
	private const string BoxInitText = "3x";

	/// <summary>日志里用的中文名（**不显示在界面上** —— 界面显示实际倍率）。</summary>
	private const string BoxText = "双倍加速";

	/// <summary>
	/// 诊断日志总开关。排查时置 true 重新编译。
	/// ⚠️ `Diag()` 方法体内**必须**是 `GD.Print`，绝不能写成 `Diag` ——
	///   本项目踩过：脚本批量替换时把方法体自己也换了 ⇒ 无限递归 ⇒
	///   `StackOverflowException`（catch 抓不住，直接终止线程）。
	/// </summary>
	private static readonly bool EnableDiagLog = true;

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

	/// <summary>
	/// 类型查找缓存（v1.0.3）。
	/// `VanillaAccelScale()` 现在要按帧调用（算文本），而它走 `GetSingleton()`
	/// ⇒ `AppDomain.GetAssemblies()` + `Assembly.GetType()` 每帧跑一遍太浪费。
	/// 这里缓存"已找到"的类型；**只缓存成功结果**，避免在程序集尚未加载时把 null 缓存住。
	/// </summary>
	private static readonly System.Collections.Generic.Dictionary<string, Type> _typeCache
		= new System.Collections.Generic.Dictionary<string, Type>();

	// ================================================================ 入口三回调

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			string root = (context == null) ? "<null>" : context.PackageRoot;
			Info("初始化完成；PackageRoot=" + root
				+ "。将在「加速」左侧新增「" + BoxText + "」勾选框（= 原加速 ×"
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

	private void OnProcessFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			CheckBox vanilla = FindVanillaBox();
			if (vanilla == null)
			{
				// 不在战斗场景（主菜单、关卡选择…）⇒ 收起自己的按钮
				if (_box != null && GodotObject.IsInstanceValid(_box))
				{
					_box.Visible = false;
				}
				if (!_reportedNoVanilla)
				{
					_reportedNoVanilla = true;
					Diag("未找到游戏自带「加速」复选框（当前不在战斗场景）⇒ 本勾选框隐藏。");
				}
				return;
			}
			_reportedNoVanilla = false;
			_vanilla = vanilla;

			EnsureBox(vanilla);
			SyncBox(vanilla);
			EnforceMutualExclusion();     // 反方向互斥：玩家点了原「加速」⇒ 取消我方
		}
		catch (Exception ex)
		{
			Warn("每帧维护异常（只报一次）：" + ex.Message);
		}
	}

	/// <summary>
	/// 找游戏自带「加速」复选框。
	///
	/// 路径取自 `Scene/TowerDefesne/TowerDefenseNew/TowerDefenseControlNew.tscn`：
	///     [node name="CheckBox2X" type="CheckBox" parent="GUITop"]
	/// 但**不写死整条路径** —— 逐级按类名找 `TowerDefenseControl` 节点更稳
	/// （关卡编辑器等场景可能换皮），拿到后再按 `checkBox2X` 字段名找子节点。
	/// </summary>
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
	/// 每帧把自建勾选框摆到「加速」左侧，并与它的可见性保持一致。
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
			_on = pressed;
			if (pressed)
			{
				// ① 取消原有加速（互斥）
				SetVanillaPressed(false);
				// ② 在"原加速档位"基础上再乘 DoubleFactor
				ApplyTimeScale(DoubleFactor);
				PlaySound("2XSpeedOn");
				Info("开启" + BoxText + "：TimeScale → " + CurrentTimeScaleText());
			}
			else
			{
				// 取消 ⇒ 回到普通速度（此时原有加速已被我们关掉）
				ApplyTimeScale(1.0 / DoubleFactor);
				PlaySound("2XSpeedDown");
				Info("关闭" + BoxText + "：TimeScale → " + CurrentTimeScaleText());
			}
			// 取消本框时，若原加速曾被我们关掉，保持关掉状态（用户要自己重新点）
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
		try
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
			// 测验关（QUIZ）用 3.0，其余 1.5 —— 与 CheckBox2XToggled 完全一致
			double mult = IsQuizLevel() ? 3.0 : 1.5;
			return baseTs * mult;
		}
		catch { return 1.5; }
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
		try
		{
			double want = VanillaAccelScale() * factor;
			object g = GetSingleton("Global");
			if (g == null)
			{
				return;
			}
			PropertyInfo p = g.GetType().GetProperty("TimeScale",
				BindingFlags.Public | BindingFlags.Static)
				?? g.GetType().GetProperty("timeScale",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (p != null && p.CanWrite)
			{
				if (p.PropertyType == typeof(double))
				{
					p.SetValue(p.GetGetMethod(true).IsStatic ? null : g, want);
				}
				else if (p.PropertyType == typeof(float))
				{
					p.SetValue(p.GetGetMethod(true).IsStatic ? null : g, (float)want);
				}
				if (Math.Abs(_lastLoggedBase - want) > 0.0001)
				{
					_lastLoggedBase = want;
					Diag("TimeScale 设为 " + want.ToString("0.###")
						+ "（原加速档=" + VanillaAccelScale().ToString("0.###")
						+ " × " + factor.ToString("0.##") + "）");
				}
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
			if (v.ButtonPressed && _on)
			{
				// 玩家点了原加速 ⇒ 取消我方（不调 ApplyTimeScale，让游戏的逻辑自己生效）
				_suppress = true;
				_on = false;
				if (_box != null && GodotObject.IsInstanceValid(_box))
				{
					_box.ButtonPressed = false;
				}
				Info("检测到「加速」被勾选 ⇒ 自动取消" + BoxText + "（互斥）。");
			}
		}
		catch { }
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
		try
		{
			if (_context != null) { _context.Log(LogPrefix + msg); }
			else { GD.Print(LogPrefix + msg); }
		}
		catch { }
	}

	private void Warn(string msg)
	{
		try
		{
			if (_context != null) { _context.Warn(LogPrefix + msg); }
			else { GD.PrintErr(LogPrefix + msg); }
		}
		catch { }
	}
}
