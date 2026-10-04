namespace PgrVoice;

public sealed partial class PlaybackEngine
{
    /// <summary>
    /// 用户明确确认恢复的当前位置。保持路线、单句限制、履历与撤销点，
    /// 不重新记录这句、不播放，也不越过原声或未核实的边界状态。
    /// </summary>
    public bool ConfirmCurrentPosition()
    {
        if (Mode == RunMode.Original) return FailNavigation("游戏原声时段需要明确选择续接台词。");
        if (Current?.Kind != "line" || Mode is not (RunMode.Ready or RunMode.Paused or RunMode.Following))
            return FailNavigation("请先确认游戏中的当前台词或分支位置。");
        if (!Allowed(Current) && !(singleLine && CanLocate(Current)))
            return FailNavigation("当前位置已不在允许的路线中，请重新定位。");
        if (Mode != RunMode.Following)
        {
            StopRequested?.Invoke(); Mode = RunMode.Following;
            Notice = singleLine ? "当前位置已确认；仍只播放这一句，下一次推进返回所属菜单。" : "当前位置已确认，等待下一次推进。";
            NavigationError = ""; Changed?.Invoke();
        }
        return true;
    }
}
