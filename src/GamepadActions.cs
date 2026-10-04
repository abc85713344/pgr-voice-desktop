using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PgrVoice;

internal static class GamepadLayout
{
    public static Dictionary<string, string> Defaults() => new()
    {
        ["panel"]="Start", ["pause"]="East", ["replay"]="West", ["ocr"]="North",
        ["previous"]="DPadLeft", ["manualNext"]="DPadRight", ["interactions"]="DPadUp",
        ["history"]="DPadDown", ["automatic"]="RightShoulder", ["original"]="None"
    };
    public static readonly GamepadButtons[] Choices = { GamepadButtons.None, GamepadButtons.South, GamepadButtons.East,
        GamepadButtons.West, GamepadButtons.North, GamepadButtons.Start, GamepadButtons.Back,
        GamepadButtons.LeftShoulder, GamepadButtons.RightShoulder, GamepadButtons.LeftStick, GamepadButtons.RightStick,
        GamepadButtons.DPadUp, GamepadButtons.DPadDown, GamepadButtons.DPadLeft, GamepadButtons.DPadRight,
        GamepadButtons.LeftTrigger, GamepadButtons.RightTrigger };
    public static GamepadButtons Parse(string? text) => Enum.TryParse<GamepadButtons>(text, out var value) && Choices.Contains(value) ? value : GamepadButtons.None;
    public static string Label(GamepadButtons button, bool sony) => button switch
    {
        GamepadButtons.None => "未绑定", GamepadButtons.South => sony ? "× 叉" : "A",
        GamepadButtons.East => sony ? "○ 圆" : "B", GamepadButtons.West => sony ? "□ 方块" : "X",
        GamepadButtons.North => sony ? "△ 三角" : "Y", GamepadButtons.Back => sony ? "Share / Create" : "View（双框）",
        GamepadButtons.Start => sony ? "Options" : "Menu（三横）",
        GamepadButtons.LeftShoulder => sony ? "L1" : "LB", GamepadButtons.RightShoulder => sony ? "R1" : "RB",
        GamepadButtons.LeftTrigger => sony ? "L2" : "LT", GamepadButtons.RightTrigger => sony ? "R2" : "RT",
        GamepadButtons.LeftStick => sony ? "L3" : "左摇杆按下", GamepadButtons.RightStick => sony ? "R3" : "右摇杆按下",
        GamepadButtons.DPadUp => "↑", GamepadButtons.DPadDown => "↓", GamepadButtons.DPadLeft => "←", GamepadButtons.DPadRight => "→",
        _ => button.ToString()
    };
    public static string Describe(GamepadButtons buttons, bool sony) => buttons == GamepadButtons.None ? "已松开" :
        string.Join(" + ", Choices.Where(x => x != GamepadButtons.None && (buttons & x) != 0).Select(x => Label(x, sony)));
}

// 每次离开作用域、断开或切设备后重新等待归中。离散动作在松开后提交，避免
// 确认分支时把仍按住的 A/× 带回游戏；游戏对白跟随使用按下时刻，供画面检查
// 对照输入前的帧。方向导航允许有节制地重复，播放动作不重复。
internal sealed class GamepadActionGate
{
    GamepadButtons previous, direction, stickDirection;
    string? pending;
    bool armed, chordUsed, ambiguous;
    long repeatAt;
    public GamepadButtons Current { get; private set; }
    public void Reset()
    { previous = Current = direction = stickDirection = GamepadButtons.None; pending = null; armed = chordUsed = ambiguous = false; repeatAt = 0; }
    static bool One(GamepadButtons value) { ulong bits = (ulong)value; return bits != 0 && (bits & (bits - 1)) == 0; }
    public GamepadButtons WithStick(GamepadButtons buttons, float x, float y)
    {
        float magnitude = Math.Max(Math.Abs(x), Math.Abs(y));
        if (magnitude < .30f) stickDirection = GamepadButtons.None;
        else if (magnitude >= .60f)
            stickDirection = Math.Abs(x) > Math.Abs(y) ? (x < 0 ? GamepadButtons.DPadLeft : GamepadButtons.DPadRight) :
                (y < 0 ? GamepadButtons.DPadUp : GamepadButtons.DPadDown);
        return buttons | stickDirection;
    }
    public string? Read(GamepadButtons buttons, long now, bool panel, GamepadButtons modifier,
        IReadOnlyDictionary<string,string> bindings, GamepadButtons advance)
    {
        Current = buttons;
        if (!armed)
        { previous = buttons; if (buttons == GamepadButtons.None) armed = true; return null; }
        var pressed = buttons & ~previous; previous = buttons;
        if (buttons == GamepadButtons.None)
        {
            var result = ambiguous ? null : pending;
            pending = null; direction = GamepadButtons.None; chordUsed = ambiguous = false; repeatAt = 0;
            return result;
        }
        if (modifier != GamepadButtons.None && (buttons & modifier) != 0)
        {
            if(!chordUsed)pending=null;
            chordUsed = true; direction = GamepadButtons.None; repeatAt = 0;
            var other = buttons & ~modifier;
            if (other == GamepadButtons.None) return null;
            if (!One(other)) { pending = null; ambiguous = true; return null; }
            string? action = bindings.FirstOrDefault(x => GamepadLayout.Parse(x.Value) == other).Key;
            if (pressed != GamepadButtons.None && !ambiguous) pending = action;
            return null;
        }
        if (chordUsed) return null; // 先松开修饰键也不得把尾键当裸按键执行。
        if (!One(buttons)) { pending = null; ambiguous = true; direction = GamepadButtons.None; return null; }
        if (ambiguous) return null;
        if (panel && Direction(buttons) is string navigation)
        {
            pending = null;
            if (direction != buttons) { direction = buttons; repeatAt = now + Stopwatch.Frequency * 45 / 100; return navigation; }
            return null;
        }
        direction = GamepadButtons.None;
        if (pressed == GamepadButtons.None) return null;
        if(!panel) {pending=null;return buttons==advance && advance!=GamepadButtons.None?"next":null;}
        pending = panel ? buttons switch
        {
            GamepadButtons.South => "confirm", GamepadButtons.East => "back", GamepadButtons.West => "replay",
            GamepadButtons.North => "bookmark", GamepadButtons.Start => "pause",
            GamepadButtons.LeftShoulder => "tabPrevious", GamepadButtons.RightShoulder => "tabNext", _ => null
        } : buttons == advance && advance != GamepadButtons.None ? "next" : null;
        return null;
    }
    public string? Repeat(long now)
    {
        if (!armed || direction == GamepadButtons.None || now < repeatAt) return null;
        repeatAt = now + Stopwatch.Frequency * 12 / 100;
        return Direction(direction);
    }
    static string? Direction(GamepadButtons buttons) => buttons switch
    { GamepadButtons.DPadUp => "up", GamepadButtons.DPadDown => "down", GamepadButtons.DPadLeft => "left", GamepadButtons.DPadRight => "right", _ => null };
}
