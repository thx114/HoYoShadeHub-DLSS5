using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using System.Collections.Generic;


namespace HoYoShadeHub.Features.GameSelector;

public class GameBizDisplay
{

    public GameInfo GameInfo { get; set; }


    public List<GameBizIcon> Servers { get; set; } = new();


    /// <summary>用户自己加的游戏（Hub 数据库里没有的）</summary>
    public bool IsCustom { get; set; }


    /// <summary>自定义卡片对应的持久化条目；已知游戏为 null</summary>
    public GameEntry? CustomEntry { get; set; }

}
