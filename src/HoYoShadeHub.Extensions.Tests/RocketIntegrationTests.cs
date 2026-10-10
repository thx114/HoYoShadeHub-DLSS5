using System.Text;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;
internal static class RocketIntegrationTests
{
    public static (int Passed, int Failed) Run()
    {
        int passed=0,failed=0;
        void Check(bool ok,string name){Console.WriteLine($"  [{(ok?"PASS":"FAIL")}] {name}");if(ok)passed++;else failed++;}
        string root=Path.Combine(Path.GetTempPath(),"hysx-rocket-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string bridge=Path.Combine(root,"桥 模块","Dx11FsrBridge.dll"),opti=Path.Combine(root,"Opt","OptiScaler.dll"),shade=Path.Combine(root,"Shade","ReShade64.dll");
        foreach(string path in new[]{bridge,opti,shade}){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,[1,2,3]);}
        string config=Path.Combine(root,"config.ini");
        string template="; keep comment\r\n[注入设置]\r\n原神主DLL注入模式 = 0\r\n原神第三方DLL启用 = false\r\n原神加载滤镜 = false\r\n[用户设置]\r\n原神DLL路径 = old.dll\r\n原神过滤授权 = true\r\nTOKEN = untouched-test-value\r\n[其他]\r\n原神DLL路径 = must-not-touch\r\n";
        try
        {
            Check(RocketIntegration.SupportsGame("hk4e_cn"),"原神国服可准备 Rocket");
            Check(RocketIntegration.SupportsGame("hk4e_global"),"原神国际服可准备 Rocket");
            Check(!RocketIntegration.SupportsGame("hkrpg_cn")&&!RocketIntegration.SupportsGame("nap_cn")&&!RocketIntegration.SupportsGame(null),"其他游戏不误用原神桥");
            byte[] original=Encoding.UTF8.GetBytes(template);
            byte[] modified=RocketIntegration.BuildRocketConfiguration(original,bridge);
            string output=Encoding.UTF8.GetString(modified);
            string expected=template.Replace("原神第三方DLL启用 = false","原神第三方DLL启用 = true").Replace("原神DLL路径 = old.dll","原神DLL路径 = "+bridge);
            Check(output==expected,"只改 Rocket 两个额外 DLL 键；其他行逐字节保留");
            Check(output.Contains("原神主DLL注入模式 = 0")&&output.Contains("原神加载滤镜 = false"),"不改 GIMI 注入模式或 Rocket 滤镜开关");
            Check(output.Contains("原神过滤授权 = true")&&output.Contains("TOKEN = untouched-test-value"),"不修改网络或授权设置");
            Check(output.Contains("[其他]\r\n原神DLL路径 = must-not-touch"),"相同键名的其他节不修改");
            Check(modified[0]!=0xEF,"无 BOM 的配置仍无 BOM");
            byte[] withBom=Encoding.UTF8.GetPreamble().Concat(original).ToArray();
            byte[] bomOutput=RocketIntegration.BuildRocketConfiguration(withBom,bridge);
            Check(bomOutput.Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()),"保留 UTF8 BOM");
            string unix=template.Replace("\r\n","\n");
            string unixOutput=Encoding.UTF8.GetString(RocketIntegration.BuildRocketConfiguration(Encoding.UTF8.GetBytes(unix),bridge));
            Check(!unixOutput.Contains('\r'),"不重写换行格式");
            Check(RocketIntegration.BuildRocketConfiguration(modified,bridge).SequenceEqual(modified),"配置修改幂等");
            void Reject(string text,string label)
            {
                bool rejected=false;try{RocketIntegration.BuildRocketConfiguration(Encoding.UTF8.GetBytes(text),bridge);}catch(InvalidDataException){rejected=true;}Check(rejected,label);
            }
            Reject(template.Replace("原神DLL路径 = old.dll\r\n",""),"缺路径键时不猜未知格式");
            Reject(template.Replace("原神第三方DLL启用 = false\r\n",""),"缺启用键时拒绝写入");
            Reject(template.Replace("原神DLL路径 = old.dll","原神DLL路径 = old.dll\r\n原神DLL路径 = duplicate.dll"),"重复路径键拒绝歧义");
            Reject(template.Replace("原神第三方DLL启用 = false","原神第三方DLL启用 = false\r\n原神第三方DLL启用=true"),"重复启用键拒绝歧义");
            bool badEncoding=false;try{RocketIntegration.BuildRocketConfiguration([0xFF,0xFE,0],bridge);}catch(DecoderFallbackException){badEncoding=true;}Check(badEncoding,"非 UTF8 配置不静默损坏");
            File.WriteAllBytes(config,original);
            var prepared=RocketIntegration.Prepare(config,bridge);
            Check(prepared.Success&&prepared.Message==RocketIntegration.WaitingText,"准备成功返回等待 Rocket 文案");
            Check(prepared.BackupPath is not null&&File.ReadAllBytes(prepared.BackupPath).SequenceEqual(original),"写前备份原 Rocket 配置");
            Check(File.ReadAllBytes(config).SequenceEqual(modified),"落盘内容与两键修改一致");
            var again=RocketIntegration.Prepare(config,bridge);
            Check(again.Success&&again.BackupPath is null,"重复准备不创建多余备份");
            Check(!RocketIntegration.Prepare(Path.Combine(root,"missing.ini"),bridge).Success,"配置不存在明确失败");
            var invalidBridge=RocketIntegration.Prepare(config,Path.Combine(root,"no","Dx11FsrBridge.dll"));
            Check(!invalidBridge.Success&&File.ReadAllBytes(config).SequenceEqual(modified),"桥不存在不修改 Rocket");
            File.WriteAllText(config,"[unknown]\nkey=value\n");
            byte[] invalid=File.ReadAllBytes(config);var unknown=RocketIntegration.Prepare(config,bridge);
            Check(!unknown.Success&&File.ReadAllBytes(config).SequenceEqual(invalid),"未知 Rocket 配置保持原样");
            var steps=RocketIntegration.BuildGraphicsChain(bridge,opti,shade);
            Check(steps.SequenceEqual(new[]{"wait dxgi.dll","load "+opti,"wait OptiScaler.dll","load "+shade}),"完整图形链顺序为 DXGI→Opt→ReShade");
            Check(steps.All(s=>!s.StartsWith("migoto ")&&!s.Contains("GIMI")),"链内绝不加载第二份 GIMI");
            Check(steps.Where(s=>s.StartsWith("load ")).All(s=>Path.IsPathFullyQualified(s[5..])),"加载目标使用绝对路径，不依赖 Rocket 工作目录");
            var chain=RocketIntegration.EnsureGraphicsChain(bridge,opti,shade);
            Check(chain.Success&&File.Exists(chain.ConfigPath),"清单不存在时创建 Dx11FsrBridge.chain.txt");
            byte[] content=File.ReadAllBytes(chain.ConfigPath!);
            Check(content[0]!=0xEF,"桥清单无 BOM");
            Check(File.ReadAllText(chain.ConfigPath!).Contains("load "+shade),"清单包含所选 ReShade DLL");
            Check(RocketIntegration.EnsureGraphicsChain(bridge,opti,shade).BackupPath is null,"同一加载链重复准备幂等");
            File.WriteAllText(chain.ConfigPath!,"migoto original.dll\n");
            var repaired=RocketIntegration.EnsureGraphicsChain(bridge,opti,shade);
            Check(repaired.Success&&repaired.BackupPath is not null&&File.ReadAllText(repaired.BackupPath)=="migoto original.dll\n","清理旧 migoto 链前备份");
            Check(!File.ReadAllText(chain.ConfigPath!).Contains("migoto "),"Rocket 模式强制恢复为无 GIMI 图形链");
            var noOpt=RocketIntegration.BuildGraphicsChain(bridge,null,shade);
            Check(noOpt.SequenceEqual(new[]{"wait dxgi.dll","load "+shade}),"Opt 关闭时链不偷偷加载 Opt");
            var noShade=RocketIntegration.BuildGraphicsChain(bridge,opti,null);
            Check(noShade.Count==3&&noShade.All(s=>!s.Contains("ReShade")),"Shade 关闭时不偷偷加载 ReShade");
            Check(!RocketIntegration.EnsureGraphicsChain(bridge,null,null).Success,"没有选图形组件时不假报已就绪");
            Check(!RocketIntegration.EnsureGraphicsChain(bridge,Path.Combine(root,"missing","OptiScaler.dll"),shade).Success,"所选 Opt 缺失时准备失败");
            Check(File.ReadAllBytes(bridge).SequenceEqual(new byte[]{1,2,3})&&File.ReadAllBytes(opti).SequenceEqual(new byte[]{1,2,3})&&File.ReadAllBytes(shade).SequenceEqual(new byte[]{1,2,3}),"准备过程不修改任何 DLL");
            Check(RocketIntegration.FindConfigPath(config)==Path.GetFullPath(config),"用户保存位置优先解析");
            Check(RocketIntegration.FindConfigPath(Path.Combine(root,"bad.ini")) is null,"保存位置失效不偷偷使用另一份 Rocket");

            // 多游戏：火箭后端 + XXMI 导入器都有的才算支持（键前缀 = 火箭配置里的中文游戏名）
            Check(RocketIntegration.GameNameFor("hk4e_cn","原神")=="原神","火箭配置名：原神");
            Check(RocketIntegration.GameNameFor("hkrpg_cn",null)=="崩坏：星穹铁道","火箭配置名：崩坏：星穹铁道");
            Check(RocketIntegration.GameNameFor("nap_cn",null)=="绝区零","火箭配置名：绝区零");
            Check(RocketIntegration.GameNameFor(null,"Wuthering Waves")=="鸣潮","自定义游戏按名字认：鸣潮");
            Check(RocketIntegration.GameNameFor(null,"Arknights: Endfield")=="终末地","自定义游戏按名字认：终末地");
            Check(RocketIntegration.GameNameFor(null,"异环") is null,"异环：XXMI 没有导入器，不收");
            Check(!RocketIntegration.SupportsRocketGame("bh3_cn","崩坏3"),"崩坏3：火箭没有后端，不收");
            Check(RocketIntegration.SupportsGame("hk4e_cn")&&!RocketIntegration.SupportsGame("hkrpg_cn"),"SupportsGame 仍然只表示原神桥");

            // 插件 DLL 列表：<游戏名>插件DLL列表 / <游戏名>插件启用列表，| 分隔、顺序即注入顺序
            string pluginTemplate="; keep\r\n[用户设置]\r\n仅显示有Mod角色 = true\r\n原神插件DLL列表 = old1|old2\r\n原神插件启用列表 = 0|0\r\n原神过滤授权 = true\r\n[其他]\r\n原神插件DLL列表 = must-not-touch\r\n";
            string[] wanted=[bridge,opti,shade];
            byte[] pluginOut=RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(pluginTemplate),"原神",wanted);
            string pluginText=Encoding.UTF8.GetString(pluginOut);
            Check(pluginText.Contains("原神插件DLL列表 = "+string.Join('|',wanted.Select(Path.GetFullPath))),"插件 DLL 列表按顺序写成 | 分隔的绝对路径");
            Check(pluginText.Contains("原神插件启用列表 = 1|1|1"),"启用列表跟着列表长度写 1");
            Check(pluginText.Contains("[其他]\r\n原神插件DLL列表 = must-not-touch"),"别的节里同名键不动");
            Check(pluginText.Contains("; keep")&&pluginText.Contains("原神过滤授权 = true"),"注释与其他键逐字节保留");
            Check(RocketIntegration.BuildPluginListConfiguration(pluginOut,"原神",wanted).SequenceEqual(pluginOut),"插件列表写入幂等");

            // 键不存在时插到 [用户设置] 节末尾，不越到下一个节
            string bare="[原神]\r\n原神包名 = YuanShen.exe\r\n\r\n[用户设置]\r\n软件主题 = 1\r\n\r\n[用户选择]\r\n游戏选择 = 3\r\n";
            string bareOut=Encoding.UTF8.GetString(RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(bare),"鸣潮",[opti]));
            Check(bareOut.Contains("[用户设置]\r\n软件主题 = 1\r\n鸣潮插件DLL列表 = "+Path.GetFullPath(opti)+"\r\n鸣潮插件启用列表 = 1\r\n\r\n[用户选择]"),"缺键时插到 [用户设置] 节末尾，不越节");
            void RejectPlugin(string text,string label)
            {
                bool rejected=false;try{RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(text),"原神",[bridge]);}catch(InvalidDataException){rejected=true;}Check(rejected,label);
            }
            RejectPlugin("[原神]\r\n原神包名 = a\r\n","没有 [用户设置] 节时拒绝写入");
            RejectPlugin("[用户设置]\r\n原神插件DLL列表 = a\r\n原神插件DLL列表 = b\r\n","重复插件列表键拒绝歧义");
            bool missingDll=false;
            try{RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(bare),"鸣潮",[Path.Combine(root,"nope.dll")]);}catch(FileNotFoundException){missingDll=true;}
            Check(missingDll,"列表里的 DLL 不存在时拒绝写入");
        }
        finally
        {
            // All test files are bounded by this explicitly created temp fixture.
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("fixture path escaped temp root");
            Directory.Delete(root,true);
        }
        Console.WriteLine($"Rocket integration: PASS {passed} / FAIL {failed}; no process/injector started.");
        return (passed,failed);
    }
}
