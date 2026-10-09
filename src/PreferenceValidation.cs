using System;
using System.Linq;

namespace PgrVoice;

/// <summary>读取偏好后、建立控件前补齐旧配置。只修设置值，不重写旧剧情位置或选择。</summary>
public static class PreferenceValidation
{
    public static bool Normalize(Preferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool changed = false;
        void Set<T>(T current, T next, Action<T> assign)
        {
            if (Equals(current, next)) return;
            assign(next); changed = true;
        }
        if (value.Visits == null) { value.Visits = new(); changed = true; }
        if (value.Facts == null) { value.Facts = new(); changed = true; }
        if (value.Heard == null) { value.Heard = new(); changed = true; }
        if (value.MenuSelections == null) { value.MenuSelections = new(); changed = true; }
        if (value.Choices == null) { value.Choices = new(); changed = true; }
        if (value.SpeakerVolumes == null) { value.SpeakerVolumes = new(); changed = true; }
        if (value.Keys == null) { value.Keys = new(); changed = true; }
        if (value.GamepadBindings == null) { value.GamepadBindings = GamepadLayout.Defaults(); changed = true; }
        Set(value.PackFile, value.PackFile ?? "", x => value.PackFile = x);
        Set(value.PackId, value.PackId ?? "", x => value.PackId = x);
        Set(value.OutputDeviceId, value.OutputDeviceId ?? "", x => value.OutputDeviceId = x);
        Set(value.GameProcess, value.GameProcess ?? "", x => value.GameProcess = x);
        Set(value.GameExecutablePath, value.GameExecutablePath ?? "", x => value.GameExecutablePath = x);
        Set(value.GamepadDeviceName, value.GamepadDeviceName ?? "", x => value.GamepadDeviceName = x);
        Set(value.CompactMode, value.CompactMode is "ball" or "strip" ? value.CompactMode : "ball", x => value.CompactMode = x);
        Set(value.GamepadGlyphStyle, value.GamepadGlyphStyle is "auto" or "xbox" or "playstation" ? value.GamepadGlyphStyle : "auto", x => value.GamepadGlyphStyle = x);
        // 和原 InitializeGamepad 的回退一致；不调整用户已有的合法键或冲突关系。
        if (GamepadLayout.Parse(value.GamepadModifier) == GamepadButtons.None)
            Set(value.GamepadModifier, "Back", x => value.GamepadModifier = x);
        if (GamepadLayout.Parse(value.GamepadAdvanceButton) == GamepadButtons.None)
            Set(value.GamepadAdvanceButton, "South", x => value.GamepadAdvanceButton = x);
        // null 原先不匹配任何按键；显式 None 保持同样不触发，不能替用户启用一个默认动作。
        foreach (var key in value.Keys.Where(x => x.Value == null).Select(x => x.Key).ToArray())
        { value.Keys[key] = "None"; changed = true; }
        foreach (var key in value.GamepadBindings.Where(x => x.Value == null).Select(x => x.Key).ToArray())
        { value.GamepadBindings[key] = "None"; changed = true; }
        Set(value.Volume, double.IsFinite(value.Volume) ? Math.Clamp(value.Volume, 0, 100) : 80, x => value.Volume = x);
        foreach (var key in value.SpeakerVolumes.Keys.ToArray())
        {
            int original = value.SpeakerVolumes[key], percent = Math.Clamp(original, 0, 100);
            if (original != percent) { value.SpeakerVolumes[key] = percent; changed = true; }
        }
        Set(value.ReadingScale, new[] { 1d, 1.25, 1.5, 2d }.Contains(value.ReadingScale) ? value.ReadingScale : 1, x => value.ReadingScale = x);
        Set(value.AutomaticDelaySeconds, new[] { .6, 1, 1.5, 2 }.Contains(value.AutomaticDelaySeconds) ? value.AutomaticDelaySeconds : 1, x => value.AutomaticDelaySeconds = x);
        Set(value.TextAutoDelaySeconds, new[] { 1d, 1.5, 2d }.Contains(value.TextAutoDelaySeconds) ? value.TextAutoDelaySeconds : 1.5, x => value.TextAutoDelaySeconds = x);
        Set(value.Left, double.IsFinite(value.Left) ? value.Left : 60, x => value.Left = x);
        Set(value.Top, double.IsFinite(value.Top) ? value.Top : 100, x => value.Top = x);
        Set(value.PanelWidth, double.IsFinite(value.PanelWidth) && value.PanelWidth > 0 ? value.PanelWidth : 580, x => value.PanelWidth = x);
        Set(value.PanelHeight, double.IsFinite(value.PanelHeight) && value.PanelHeight > 0 ? value.PanelHeight : 760, x => value.PanelHeight = x);
        // 负坐标可以是左侧或上方副屏；只把非有限值还原成原本的“尚未定位”哨兵。
        Set(value.ClickZoneLeft, double.IsFinite(value.ClickZoneLeft) ? value.ClickZoneLeft : -1, x => value.ClickZoneLeft = x);
        Set(value.ClickZoneTop, double.IsFinite(value.ClickZoneTop) ? value.ClickZoneTop : -1, x => value.ClickZoneTop = x);
        if (value.DialogueRegion == null || !value.DialogueRegion.IsValid)
        { value.DialogueRegion = new(); changed = true; }
        return changed;
    }
}
