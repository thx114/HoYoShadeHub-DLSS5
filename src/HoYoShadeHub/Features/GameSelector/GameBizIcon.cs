using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using System;


namespace HoYoShadeHub.Features.GameSelector;

public partial class GameBizIcon : ObservableObject, IEquatable<GameBizIcon>
{


    private const double GB = 1 << 30;


    public GameId GameId { get; set; }

    public GameBiz GameBiz { get; set; }


    public string GameIcon { get; set => SetProperty(ref field, value); }

    public string GameName { get; set => SetProperty(ref field, value); }

    public string ServerIcon { get; set => SetProperty(ref field, value); }

    public string ServerName { get; set => SetProperty(ref field, value); }

    public double MaskOpacity { get; set => SetProperty(ref field, value); } = 1.0;

    /// <summary>在顶部那行里（= 已固定）。右键菜单靠它决定显示「固定」还是「取消固定」</summary>
    public bool IsPinned
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(NotPinned));
            }
        }
    }

    public bool NotPinned => !IsPinned;

    public string? InstallPath { get; set => SetProperty(ref field, value); }

    public long TotalSize { get; set { field = value; OnPropertyChanged(nameof(TotalSizeText)); } }

    public string? TotalSizeText => TotalSize == 0 ? null : $"{TotalSize / GB:F2}GB";


    /// <summary>用户自己加的游戏（不是 Hub 已知的那些）</summary>
    public bool IsCustom { get; set; }

    /// <summary>自定义游戏对应的条目；已知游戏为 null</summary>
    public GameEntry? CustomEntry { get; set; }


    public bool IsSelected
    {
        get;
        set
        {
            field = value;
            MaskOpacity = value ? 0 : 1;
        }
    }



    public GameBizIcon(GameBiz gameBiz)
    {
        GameBiz = gameBiz;
        GameId = GameId.FromGameBiz(gameBiz)!;
        GameIcon = GameBizToIcon(gameBiz);
        ServerIcon = GameBizToServerIcon(gameBiz);
        GameName = gameBiz.ToGameName();
        ServerName = gameBiz.ToGameServerName();
    }



    public GameBizIcon(GameInfo gameInfo)
    {
        GameId = gameInfo;
        GameBiz = gameInfo.GameBiz;
        GameIcon = gameInfo.Display.Icon.Url;
        ServerIcon = GameBizToServerIcon(gameInfo.GameBiz);
        GameName = gameInfo.Display.Name;
        ServerName = gameInfo.GameBiz.ToGameServerName();
    }



    /// <summary>自定义游戏（用户自己选的 exe，Hub 不认识）</summary>
    public GameBizIcon(GameEntry entry)
    {
        CustomEntry = entry;
        IsCustom = true;

        // 用合成 biz 当身份 —— 这样 AppConfig 里那些 per-biz 设置、以及「上次选的是谁」都能直接用
        GameBiz = entry.CustomBiz;
        GameId = new GameId { Id = entry.CustomBizValue, GameBiz = GameBiz };

        GameName = entry.DisplayName;
        ServerName = "自定义";
        InstallPath = entry.GameDirectory;
        ServerIcon = "ms-appx:///Assets/Image/Transparent.png";
        GameIcon = GameIconExtractor.GetCached(entry) ?? GameIconExtractor.FallbackIcon;
    }



    public void UpdateInfo()
    {
        GameIcon = GameBizToIcon(GameBiz);
        ServerIcon = GameBizToServerIcon(GameBiz);
        GameName = GameBiz.ToGameName();
        ServerName = GameBiz.ToGameServerName();
    }


    public void UpdateInfo(GameInfo gameInfo)
    {
        GameIcon = gameInfo.Display.Icon.Url;
        ServerIcon = GameBizToServerIcon(gameInfo.GameBiz);
        GameName = gameInfo.Display.Name;
        ServerName = gameInfo.GameBiz.ToGameServerName();
    }



    private static string GameBizToIcon(GameBiz gameBiz)
    {
        return gameBiz.Game switch
        {
            GameBiz.bh3 => "ms-appx:///Assets/Image/icon_bh3.jpg",
            GameBiz.hk4e => "ms-appx:///Assets/Image/icon_ys.jpg",
            GameBiz.hkrpg => "ms-appx:///Assets/Image/icon_sr.jpg",
            GameBiz.nap => "ms-appx:///Assets/Image/icon_zzz.jpg",
            GameBiz.pp => "ms-appx:///Assets/Image/icon_pp.jpg",
            GameBiz.hna => "ms-appx:///Assets/Image/icon_hna.jpg",
            _ => "ms-appx:///Assets/Image/Transparent.png",
        };
    }


    private static string GameBizToServerIcon(GameBiz gameBiz)
    {
        return gameBiz.Server switch
        {
            "cn" => "ms-appx:///Assets/Image/gameicon_hyperion.png",
            "global" => "ms-appx:///Assets/Image/gameicon_hoyolab.png",
            "bilibili" => "ms-appx:///Assets/Image/gameicon_bilibili.png",
            "beta_prebeta" or "beta_postbeta" or "cn_beta" or "os_beta" or "beta" or "cbt1" => "ms-appx:///Assets/Image/4bb2f9e21b6c64201692c4ea76a2faca_7020708978085976578.png",
            _ => "ms-appx:///Assets/Image/Transparent.png",
        };
    }


    public bool Equals(GameBizIcon? other)
    {
        return ReferenceEquals(this, other) || GameBiz == other?.GameBiz;
    }

}
