using HoYoShadeHub.Core;

namespace HoYoShadeHub.Features.GameSelector;

/// <summary>
/// 「把这个游戏固定到顶部」—— 定位游戏成功之后发这个，
/// 顶部那行游戏图标就能自己多出一个（用户要求：定位到的游戏就固定到顶部）。
/// </summary>
internal sealed class PinGameBizMessage
{
    public PinGameBizMessage(GameBiz gameBiz)
    {
        GameBiz = gameBiz;
    }

    public GameBiz GameBiz { get; }
}
