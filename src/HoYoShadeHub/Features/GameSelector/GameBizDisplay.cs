using HoYoShadeHub.Core.HoYoPlay;
using System.Collections.Generic;


namespace HoYoShadeHub.Features.GameSelector;

public class GameBizDisplay
{

    public GameInfo GameInfo { get; set; }


    public List<GameBizIcon> Servers { get; set; } = new();


    /// <summary>用户自己加的游戏（Hub 数据库里没有的）</summary>
    public bool IsCustom { get; set; }

}
