namespace HomeAssistantAcDefender.Services;

/// <summary>
/// 口語廣東話 — app chrome: navigation, page titles, master switch, common buttons and labels.
/// Keys are the EXACT English source strings. Colloquial written Cantonese (唔/嘅/喺),
/// Traditional script; technical terms (AC, Home Assistant, setpoint numbers) stay as-is.
/// </summary>
public static class YueCommon
{
    public static void AddTo(Dictionary<string, string> map)
    {
        // ── Nav rail stations ──
        map["COMMAND"] = "指揮部";
        map["DEFENSE"] = "防衛";
        map["COMFORT"] = "舒適";
        map["ENERGY"] = "能源";
        map["LOGS"] = "紀錄";
        map["CONTROLS"] = "操控";
        map["ORDERS"] = "指令";
        map["GUIDE"] = "手冊";
        map["STATIONS"] = "崗位";

        // ── Page titles ──
        map["Command Center"] = "指揮中心";
        map["Defense Roster"] = "防衛名冊";
        map["Comfort Watch"] = "舒適哨崗";
        map["Energy Intel"] = "能源情報";
        map["Field Reports"] = "戰地報告";
        map["Direct Controls"] = "直接操控";
        map["Standing Orders"] = "長期指令";
        map["Field Manual"] = "野戰手冊";

        // ── Master switch ──
        map["MASTER SWITCH"] = "總開關";
        map["⛨ STAND DOWN"] = "⛨ 收隊";
        map["⏻ ACTIVATE"] = "⏻ 開工";
        map["The guard post is awake and caffeinated."] = "哨崗醒晒神，咖啡都飲埋。";
        map["Paused. Tap to wake the tiny guard shift."] = "暫停咗。撳一下叫班小衛兵返工。";

        // ── Mood labels ──
        map["STANDING DOWN"] = "收隊休息";
        map["GUARDS ENGAGED"] = "衛兵出動";
        map["ON WATCH"] = "當值中";
        map["HA OFFLINE"] = "HA 離線";

        // ── Common buttons / labels ──
        map["Refresh"] = "刷新";
        map["Force target"] = "強制目標";
        map["Force cooling"] = "強制降溫";
        map["Emergency"] = "緊急";
        map["Learning"] = "學習";
        map["1 hour"] = "1 個鐘";
        map["2 hours"] = "2 個鐘";
        map["4 hours"] = "4 個鐘";
        map["Wake the guards now"] = "即刻叫醒衛兵";
        map["Mess hall (siesta)"] = "飯堂（午睡）";
        map["Guards asleep"] = "衛兵瞓緊";
        map["Guards on duty"] = "衛兵當值";
        map["Off"] = "閂咗";
        map["Monthly budget"] = "每月預算";
        map["Field kitchen — rations"] = "野戰廚房 — 口糧";
        map["Energy overview"] = "能源總覽";
        map["Real thermostat actions"] = "真溫控器操作";
        map["SUMMON AI REACTOR OPERATOR — 1 ration / hour"] = "召喚 AI 反應堆操作員 — 每個鐘 1 份口糧";

        // ── Field kitchen metrics ──
        map["Pantry balance"] = "口糧倉結餘";
        map["Earned today"] = "今日賺到";
        map["Released this month"] = "今個月放出";
        map["Hot window"] = "熱窗口";
        map["Duty cycle"] = "開機比例";
        map["Rations"] = "口糧";

        // ── Cool-outdoor card metrics ──
        map["Outdoor now"] = "而家出面";
        map["Shutdown below"] = "低過就熄";
        map["Restores at"] = "回復溫度";
        map["Forecast peak"] = "預報最高";
        map["Forecast gate"] = "預報閘";
        map["Off dwell"] = "熄機最短時間";

        // ── Siesta card metrics ──
        map["Nap ends"] = "瞓到幾點";
        map["Reason"] = "原因";
        map["Rations this nap"] = "今次瞓覺賺到";
        map["Start action"] = "開始動作";
        map["Watching"] = "睇緊";
        map["Sleeping"] = "瞓緊";
        map["On duty"] = "當值";
        map["Human override"] = "有人手動改咗";
        map["AC OFF"] = "AC 熄咗";
        map["Pass"] = "通過";
        map["Blocking"] = "攔住";
        map["Stocked"] = "有貨";
        map["Empty"] = "空嘅";
        map["Paying the bill"] = "幫緊 AC 埋單";
        map["Reactor"] = "反應堆";
        map["Powered until"] = "有電到";
        map["Unpowered"] = "冇電";
    }
}
