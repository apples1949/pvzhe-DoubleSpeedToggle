# 双倍加速（DoubleSpeedToggle）

> 在游戏自带「加速」复选框的**左侧**新增一个「双倍加速」勾选框。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `doublespeedtoggle` |
| 程序集 | `JTYDoubleSpeedToggle` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `DoubleSpeedToggleEntry` |
| 当前版本 | 1.0.0 |
| 分类 | 玩法调整 |

## 行为

| 操作 | 结果 |
| --- | --- |
| 勾选「双倍加速」 | 速度 = **原加速档位 × 2**；同时**自动取消原有「加速」**（互斥，绝不叠加） |
| 再点一次取消 | 回到普通速度（1×） |
| 玩家自己勾上「加速」 | **自动取消**「双倍加速」（反方向互斥也做了） |
| 不在战斗中 | 勾选框跟随「加速」一起隐藏 |

## 为什么"双倍"就是 `Global.TimeScale × 2`

游戏原生加速的实现（`Scene/TowerDefesne/TowerDefenseControl.cs`）：

```csharp
public void CheckBox2XToggled(bool toggled)
{
    if (toggled) {
        AudioManager.Instance.AudioPlay("2XSpeedOn");
        if (levelConfig is TowerDefenseLevelConfig { finishMethod: not QUIZ } c)
            Global.TimeScale = c.baseTimeScale * 1.5;      // 普通关
        else if (levelConfig is TowerDefenseLevelConfig c2)
            Global.TimeScale = c2.baseTimeScale * 3.0;     // 测验关(QUIZ)
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
＋判 `finishMethod == "QUIZ"`），再乘 `DoubleFactor`。改档位只改一个常量：

```csharp
private const double DoubleFactor = 2.0;   // ← 想改成 3 倍就改这里
```

## 位置：为什么不会和「时停」冲突

| 元素 | 锚点 |
| --- | --- |
| 「加速」（`CheckBox2X`） | `GUITop` 下，`anchor_left=1.0`、`offset_left=-107`、`offset_top=64`（**右上角**） |
| **本 Mod 勾选框** | 每帧读「加速」的 `Position/Size/Scale`，摆在它**左侧**（同一父节点，天然跟随缩放） |
| 「时停」(TimeStop) | 挂在**左下角齿轮 `optionButton` 的右侧**（`GlobalPosition.Y + Size.Y + 6`） |

两者分别在屏幕**右上**与**左下**，相隔整屏。

## 已知坑（本项目已付出代价的）

1. **`SceneTree.ProcessFrame += callable` 会 CS0029** —— C# 侧该属性类型是 `Action`，
   而 `Callable` 不能隐式转 `Action`（只有 `Action → Callable` 这一个方向）。
   ⇒ 必须写 `_tree.Connect("process_frame", callable);`
2. **`BaseButton.Toggled` 是 C# event**，委托类型 `ToggledEventHandler`；
   直接传方法组（`box.Toggled += OnToggled;`），**不要**包 `new Action<bool>(...)`（CS0029）。
3. **手写 csproj ⇒ 没有 Godot 源生成器** ⇒ 入口类的自定义 `_Process` 永远不会被调用，
   必须挂 `SceneTree` 的信号。
4. **不要用 `Convert.*(object)`** —— 游戏附带的 .NET 是裁剪过的，
   `Convert.ToString(object)` 会抛 `Method not found`。
5. **`Node.Name` 是 `StringName`**，与 `string` 直接 `==` 在实机上不生效
   ⇒ 必须 `node.Name.ToString() == name`。
6. **`Diag()` 方法体内必须是 `GD.Print`**，绝不能写成 `Diag`（会无限递归 ⇒
   `StackOverflowException`，catch 抓不住）。

## 诊断

源码顶部：

```csharp
private static readonly bool EnableDiagLog = false;   // 置 true 重新编译即可看 TimeScale 实际取值
```

会打出 `TimeScale 设为 X（原加速档=Y × Z）`，便于核对档位是否符合预期。

## 构建

```powershell
python mods\DoubleSpeedToggle\build_and_install.py --install
```

改代码后**普通重启游戏**即可（`build_and_install.py` 会清 `ModsCache/DoubleSpeedToggle`）。
