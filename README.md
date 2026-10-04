# 三倍加速（DoubleSpeedToggle）

> 在游戏自带「加速」复选框的**正下方**新增一个勾选框，勾选后在原加速基础上再乘 2。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `doublespeedtoggle` |
| 程序集 | `JTYDoubleSpeedToggle` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `DoubleSpeedToggleEntry` |
| 当前版本 | 1.0.7 |
| 分类 | 玩法调整 |

## 名称与倍率的关系（容易混，先说清）

游戏**原生**「加速」的真实倍率**不是 2 倍，而是 1.5 倍**（名字叫 `2X` 是历史遗留）：

```
Global.TimeScale = baseTimeScale × 1.5     // 普通关
Global.TimeScale = baseTimeScale × 3.0     // finishMethod == QUIZ 的竞猜关
```

本 Mod **双倍于那个加速档**，所以在普通关卡最终是 **3 倍** —— 这也是作品名的由来。

两个勾选框的文本直接显示**实际倍率**（动态按关卡 `baseTimeScale` 计算，不写死）：

| 关卡 | 原「加速」框 | 本框 |
| --- | --- | --- |
| 绝大多数关卡（`baseTimeScale = 1.0`） | **1.5x** | **3x** |
| 小游戏 13-1~13-6、挑战·钻石 7-2（`baseTimeScale = 2.0`） | 3x | 6x |
| QUIZ 竞猜关 | 3x | 6x |

## 行为

| 操作 | 结果 |
| --- | --- |
| 勾选本框 | 速度 = **原加速档位 × 2**；同时**自动取消原有「加速」**（互斥，绝不叠加） |
| 再点一次取消 | 回到普通速度 |
| 玩家自己勾上「加速」 | **自动取消**本框（反方向互斥也做了） |
| 不在战斗中 | 勾选框跟随「加速」一起隐藏 |

## 实现：为什么"双倍"就是 `Global.TimeScale × 2`

游戏原生加速的实现（`Scene/TowerDefesne/TowerDefenseControl.cs`）：

```csharp
public void CheckBox2XToggled(bool toggled)
{
    if (toggled) {
        AudioManager.Instance.AudioPlay("2XSpeedOn");
        if (levelConfig is TowerDefenseLevelConfig { finishMethod: not QUIZ } c)
            Global.TimeScale = c.baseTimeScale * 1.5;      // 普通关
        else if (levelConfig is TowerDefenseLevelConfig c2)
            Global.TimeScale = c2.baseTimeScale * 3.0;     // 竞猜关(QUIZ)
    } else {
        AudioManager.Instance.AudioPlay("2XSpeedDown");
        Global.TimeScale = (levelConfig is TowerDefenseLevelConfig c3) ? c3.baseTimeScale : 1.0;
    }
}
```

`Core/Global/Global.cs`：

```csharp
public double timeScale { set { _timeScale = value; Engine.TimeScale = _timeScale; OnAnimeFrameRateChange?.Invoke(); } }
```

⇒ 加速**只通过 `Global.TimeScale`（引擎级时间缩放）生效**，没有第二条通道。
所以"双倍" = 把那一档再乘 2：`TimeScale = baseTimeScale × 1.5 × 2`（QUIZ 关为 `× 3 × 2`）。

本 Mod 用 `VanillaAccelScale()` **重现**这个档位计算（读 `TowerDefenseManager.currentLevelConfig.baseTimeScale`
＋判 `finishMethod == "QUIZ"`），再乘 `DoubleFactor`：

```csharp
private const double DoubleFactor = 2.0;   // ← 想改档位就改这里
```

## 位置：与「加速」「时停」的关系

```
菜单
├─ 加速 1.5x     ← 游戏自带（CheckBox2X）
├─ 3x            ← 本 Mod（紧贴「加速」视觉下边缘 +6px）
└─ 时停          ← TimeStop v1.0.31 起自动排到本框之下
```

| 元素 | 定位方式 |
| --- | --- |
| 「加速」（`CheckBox2X`） | `GUITop`（CanvasLayer）下，`anchor_left=1.0`、`offset_left=-107`、`offset_top=64`、`scale=0.78` |
| **本 Mod 勾选框** | 每帧读「加速」的全局位置/尺寸/缩放，紧贴其**视觉下边缘 +6px**，**右对齐**；每帧重算，不依赖锚点自适应 |
| 「时停」(TimeStop) | v1.0.31 起会**在「加速」下方找底边最低的 `Mod*` 控件**并锚到它之下 ⇒ 自动排到本框之后 |

> ⚠️ 两处必须**成对**：本框紧贴「加速」、时停让位。
> 若只改一边（本框让位 + 旧版时停锚「加速」），两者会**互相追逐**、一路滑出屏幕。
> 使用 **TimeStop 1.0.30 及更早**版本时，本框会与「时停」重叠。

### 尺寸与原版一致

原版「加速」带 `scale = 0.78`，所以它的字号 24 实际渲染成 `24 × 0.78 ≈ 18.7`，连勾选框方块也一起缩。
本框**连 scale 与布局尺寸一起沿用原版**，因此两个框像素级同大（只有文字内容不同）。

## 已知坑（本项目已付出代价的）

1. **`Control.Position` 是父坐标，`Offset*` 是相对锚点的量** —— 把前者当后者用会把控件摆到屏幕外
   （本项目真踩过：复选框"看不见"就是这个原因）。
   稳妥做法：**复制原件的 4 个锚点**后用偏移差定位，或统一改用 `GlobalPosition`。
2. **`SceneTree.ProcessFrame += callable` 会 CS0029** —— C# 侧该属性类型是 `Action`，
   而 `Callable` 不能隐式转 `Action`（只有 `Action → Callable` 这一个方向）。
   ⇒ 必须写 `_tree.Connect("process_frame", callable);`
3. **`BaseButton.Toggled` 是 C# event**，委托类型 `ToggledEventHandler`；
   直接传方法组（`box.Toggled += OnToggled;`），**不要**包 `new Action<bool>(...)`（CS0029）。
4. **手写 csproj ⇒ 没有 Godot 源生成器** ⇒ 入口类的自定义 `_Process` 永远不会被调用，
   必须挂 `SceneTree` 的信号。
5. **不要用 `Convert.*(object)`** —— 游戏附带的 .NET 是裁剪过的，
   `Convert.ToString(object)` 会抛 `Method not found`。
6. **`Node.Name` 是 `StringName`**，与 `string` 直接 `==` 在实机上不生效
   ⇒ 必须 `node.Name.ToString() == name`。
7. **`Diag()` 方法体内必须是 `GD.Print`**，绝不能写成 `Diag`（会无限递归 ⇒
   `StackOverflowException`，catch 抓不住）。

## 诊断

源码顶部：

```csharp
private static readonly bool EnableDiagLog = false;   // 置 true 重新编译即可看布局与 TimeScale 实际取值
```

会打出布局核对（两边的锚点/偏移/尺寸/全局位置）与 `TimeScale 设为 X（原加速档=Y × Z）`。

## 构建

```powershell
python mods\DoubleSpeedToggle\build_and_install.py --install
```

改代码后**普通重启游戏**即可（`build_and_install.py` 会清 `ModsCache/DoubleSpeedToggle`）。
