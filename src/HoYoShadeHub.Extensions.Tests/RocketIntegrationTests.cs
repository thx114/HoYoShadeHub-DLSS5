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
            // 火箭从没配置过 DLL 路径时配置项可能整条缺失：自动补全整条「键 = 值」，不再拒绝
            string noPathKey=template.Replace("原神DLL路径 = old.dll\r\n","");
            string noPathOut=Encoding.UTF8.GetString(RocketIntegration.BuildRocketConfiguration(Encoding.UTF8.GetBytes(noPathKey),bridge));
            Check(noPathOut.Contains("TOKEN = untouched-test-value\r\n原神DLL路径 = "+bridge+"\r\n[其他]"),"缺 原神DLL路径 键时整条补全到 [用户设置] 节末尾，不越节");
            string noEnabledKey=template.Replace("原神第三方DLL启用 = false\r\n","");
            string noEnabledOut=Encoding.UTF8.GetString(RocketIntegration.BuildRocketConfiguration(Encoding.UTF8.GetBytes(noEnabledKey),bridge));
            Check(noEnabledOut.Contains("原神加载滤镜 = false\r\n原神第三方DLL启用 = true\r\n[用户设置]"),"缺 原神第三方DLL启用 键时整条补全到 [注入设置] 节末尾，不越节");
            string neither=template.Replace("原神DLL路径 = old.dll\r\n","").Replace("原神第三方DLL启用 = false\r\n","");
            string neitherOut=Encoding.UTF8.GetString(RocketIntegration.BuildRocketConfiguration(Encoding.UTF8.GetBytes(neither),bridge));
            Check(neitherOut.Contains("原神DLL路径 = "+bridge)&&neitherOut.Contains("原神第三方DLL启用 = true"),"两个键都缺时一次补全两条");
            Check(RocketIntegration.BuildRocketConfiguration(Encoding.UTF8.GetBytes(neitherOut),bridge).SequenceEqual(Encoding.UTF8.GetBytes(neitherOut)),"补全后再次生成幂等");
            string emptyPath=template.Replace("原神DLL路径 = old.dll","原神DLL路径 = ");
            string emptyOut=Encoding.UTF8.GetString(RocketIntegration.BuildRocketConfiguration(Encoding.UTF8.GetBytes(emptyPath),bridge));
            Check(emptyOut.Contains("原神DLL路径 = "+bridge+"\r\n")&&!emptyOut.Contains("old.dll"),"空值键直接填入桥路径（用户手动清空场景）");
            // [注入设置] 的 <游戏名>第三方DLL启用 总开关：火箭只有它为 true 才注插件列表里的 DLL，缺失整条补全
            string enableTemplate="; c\r\n[注入设置]\r\n原神主DLL注入模式 = 0\r\n[用户设置]\r\n软件主题 = 1\r\n[其他]\r\n第三方DLL启用 = must-not-touch\r\n";
            byte[] enableBytes=RocketIntegration.BuildThirdPartyEnableConfiguration(Encoding.UTF8.GetBytes(enableTemplate),"崩坏：星穹铁道",true);
            string enableText=Encoding.UTF8.GetString(enableBytes);
            Check(enableText.Contains("原神主DLL注入模式 = 0\r\n崩坏：星穹铁道第三方DLL启用 = true\r\n[用户设置]"),"缺 崩坏：星穹铁道第三方DLL启用 键时整条补全到 [注入设置] 节末尾，不越节");
            Check(enableText.Contains("[其他]\r\n第三方DLL启用 = must-not-touch"),"总开关：其他节里相似键名不动");
            Check(RocketIntegration.BuildThirdPartyEnableConfiguration(enableBytes,"崩坏：星穹铁道",true).SequenceEqual(enableBytes),"总开关写入幂等");
            Check(Encoding.UTF8.GetString(RocketIntegration.BuildThirdPartyEnableConfiguration(enableBytes,"崩坏：星穹铁道",false)).Contains("崩坏：星穹铁道第三方DLL启用 = false\r\n"),"总开关可按游戏写回 false");
            bool enableNoSection=false;try{RocketIntegration.BuildThirdPartyEnableConfiguration(Encoding.UTF8.GetBytes("[用户设置]\r\n软件主题 = 1\r\n"),"绝区零",true);}catch(InvalidDataException){enableNoSection=true;}Check(enableNoSection,"缺 [注入设置] 节时总开关拒绝写入");

            Reject(template.Replace("[注入设置]\r\n",""),"缺 [注入设置] 节仍拒绝（多半选错了 config.ini）");
            Reject(template.Replace("[用户设置]\r\n","").Replace("原神DLL路径 = old.dll\r\n",""),"缺 [用户设置] 节仍拒绝");
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

            // 额外模块：链模式下 Hub 不开外部注入器，模块必须进链，否则「勾了等于没勾」
            string extra1=Path.Combine(root,"DLSS-NR.dll"), extra2=Path.Combine(root,"DLSS-Enabler.dll");
            File.WriteAllBytes(extra1,[0x4D,0x5A]);
            File.WriteAllBytes(extra2,[0x4D,0x5A]);
            List<string> extrasChain=RocketIntegration.BuildGraphicsChain(bridge,opti,shade,[extra1,extra2]).ToList();
            Check(extrasChain[0]=="wait dxgi.dll","加了额外模块，链第一步仍是 wait dxgi.dll");
            Check(extrasChain.IndexOf("load "+Path.GetFullPath(extra1))<extrasChain.IndexOf("load "+Path.GetFullPath(opti)),"额外模块排在 OptiScaler 之前");
            Check(extrasChain[^1]=="load "+Path.GetFullPath(shade),"加了额外模块，ReShade 仍排最后");
            Check(extrasChain.IndexOf("load "+Path.GetFullPath(extra1))<extrasChain.IndexOf("load "+Path.GetFullPath(extra2)),"额外模块之间保持传入顺序");
            Check(RocketIntegration.BuildGraphicsChain(bridge,opti,shade,[extra1,extra1]).Count==extrasChain.Count-1,"同一份额外 DLL 不重复写进链");
            var extraChain=RocketIntegration.EnsureGraphicsChain(bridge,opti,shade,[extra1,extra2]);
            Check(extraChain.Success&&File.ReadAllText(extraChain.ConfigPath??"").Contains(Path.GetFileName(extra2)),"额外模块真的落到桥的清单文件里");
            bool extraMissing=false;
            try{RocketIntegration.BuildGraphicsChain(bridge,opti,shade,[Path.Combine(root,"nope-extra.dll")]);}catch(FileNotFoundException){extraMissing=true;}
            Check(extraMissing,"额外模块文件不存在时拒绝写链");
            Check(RocketIntegration.EnsureGraphicsChain(bridge,opti,shade,[extra1,extra2]).BackupPath is null,"带额外模块的链重复准备幂等");

            // 可逆：不勾模块时重写链，上一次写进去的额外模块必须消失（不能残留）
            var rewrittenChain=RocketIntegration.EnsureGraphicsChain(bridge,opti,shade);
            string rewrittenText=File.ReadAllText(rewrittenChain.ConfigPath??"");
            Check(!rewrittenText.Contains(Path.GetFileName(extra1))&&!rewrittenText.Contains(Path.GetFileName(extra2)),"不勾模块时重写链，上次的额外模块被清掉");
            Check(rewrittenText.Contains(Path.GetFileName(opti))&&rewrittenText.Contains(Path.GetFileName(shade)),"重写链后图形层仍在");

            // 官方 XXMI 模式：额外模块进链；ReShade 只 wait 不 load（避免同进程两份）；不写 migoto
            var official=BridgeChainSteps.OfficialMode(opti,true,[extra1,extra2]);
            Check(official[0].Verb=="wait"&&official[0].Argument=="dxgi.dll","官方模式链第一步仍是 wait dxgi.dll");
            Check(!official.Any(s=>s.Verb=="load"&&s.Argument.Contains("ReShade",StringComparison.OrdinalIgnoreCase)),"官方模式链不 load ReShade（ReShade 归 inject.exe/注入规格）");
            Check(official.Any(s=>s.Verb=="wait"&&s.Argument=="ReShade64.dll"),"开了 ReShade 时链里先 wait 它");
            Check(official.FindIndex(s=>s.Verb=="load"&&s.Argument==Path.GetFullPath(opti))>official.FindIndex(s=>s.Verb=="wait"&&s.Argument=="ReShade64.dll"),"OptiScaler 排在 ReShade 之后（NR 时序）");
            Check(official.IndexOf(official.First(s=>s.Verb=="load"&&s.Argument==Path.GetFullPath(extra1)))<official.FindIndex(s=>s.Verb=="load"&&s.Argument==Path.GetFullPath(opti)),"额外模块排在 OptiScaler 之前");
            Check(!official.Any(s=>s.Verb=="migoto"),"官方模式链不写 migoto（GIMI 归 XXMI，避免两份 d3d11）");
            Check(!BridgeChainSteps.OfficialMode(opti,false,[extra1]).Any(s=>s.Verb=="wait"&&s.Argument=="ReShade64.dll"),"没开 ReShade 时不白等 15 秒");
            Check(BridgeChainSteps.OfficialMode(opti,false,[]).Count==3,"没勾其他模块且没开 ReShade 时链只有 wait dxgi → load opt → wait opt");
            Check(BridgeChainSteps.OfficialMode(opti,true,[]).Count==4,"开了 ReShade 时多一步 wait ReShade64.dll");
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
            string pluginTemplate="; keep\r\n[注入设置]\r\n原神第三方DLL启用 = false\r\n[用户设置]\r\n仅显示有Mod角色 = true\r\n原神插件DLL列表 = old1|old2\r\n原神插件启用列表 = 0|0\r\n原神过滤授权 = true\r\n[其他]\r\n原神插件DLL列表 = must-not-touch\r\n";
            string[] wanted=[bridge,opti,shade];
            byte[] pluginOut=RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(pluginTemplate),"原神",wanted);
            string pluginText=Encoding.UTF8.GetString(pluginOut);
            Check(pluginText.Contains("原神插件DLL列表 = "+string.Join('|',wanted.Select(Path.GetFullPath))),"插件 DLL 列表按顺序写成 | 分隔的绝对路径");
            Check(pluginText.Contains("原神插件启用列表 = 1|1|1"),"启用列表跟着列表长度写 1");
            Check(pluginText.Contains("原神第三方DLL启用 = true"),"[注入设置] 注入总开关同步写成 true");

            Check(pluginText.Contains("[其他]\r\n原神插件DLL列表 = must-not-touch"),"别的节里同名键不动");
            Check(pluginText.Contains("; keep")&&pluginText.Contains("原神过滤授权 = true"),"注释与其他键逐字节保留");
            Check(RocketIntegration.BuildPluginListConfiguration(pluginOut,"原神",wanted).SequenceEqual(pluginOut),"插件列表写入幂等");
            // 火箭从未配置过的占位值（多个「请填写DLL路径」形态）整体替换，不当成已配置
            string placeholder="[注入设置]\r\n原神第三方DLL启用 = true\r\n[用户设置]\r\n原神插件DLL列表 = 请填写DLL路径1|请填写DLL路径2\r\n原神插件启用列表 = 1|1\r\n软件主题 = 1\r\n";
            string placeholderOut=Encoding.UTF8.GetString(RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(placeholder),"原神",[opti]));
            Check(placeholderOut.Contains("原神插件DLL列表 = "+Path.GetFullPath(opti))&&placeholderOut.Contains("原神插件启用列表 = 1")
                &&!placeholderOut.Contains("请填写DLL路径")&&!placeholderOut.Contains("1|1"),"火箭多占位值整体替换为所选 DLL");
            // 插件列表 + 注入总开关一起补全（火箭没配置过这个游戏时两个键都可能整条缺失）
            string bare2="[注入设置]\r\n原神主DLL注入模式 = 0\r\n[用户设置]\r\n软件主题 = 1\r\n";
            string bare2Out=Encoding.UTF8.GetString(RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(bare2),"绝区零",[opti]));
            Check(bare2Out.Contains("原神主DLL注入模式 = 0\r\n绝区零第三方DLL启用 = true\r\n[用户设置]")
                &&UserValue(bare2Out,"绝区零插件DLL列表")==Path.GetFullPath(opti),"插件列表与注入总开关一起补全，总开关插到 [注入设置] 节末尾");

            // Regression: existing empty keys must never leave paths/enables on orphan lines.
            string starRail="崩坏：星穹铁道";
            string module=Path.Combine(root,"模块 with spaces","Extra Case #1.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(module)!);File.WriteAllBytes(module,[1,2,3]);
            string[] starRailDlls=[extra2,module,opti,shade];
            string starRailPaths=string.Join('|',starRailDlls.Select(Path.GetFullPath));
            string? UserValue(string text,string key)
            {
                string section="";using var reader=new StringReader(text.TrimStart('\uFEFF'));
                while(reader.ReadLine() is { } line)
                {
                    string trimmed=line.Trim();
                    if(trimmed.StartsWith('[')&&trimmed.EndsWith(']')){section=trimmed[1..^1];continue;}
                    if(section!="用户设置"||trimmed.StartsWith(';')||trimmed.StartsWith('#'))continue;
                    int eq=line.IndexOf('=');if(eq>=0&&line[..eq].Trim()==key)return line[(eq+1)..].Trim(' ','\t');
                }
                return null;
            }
            foreach(string nl in new[]{"\r\n","\n"})
            foreach(bool bom in new[]{false,true})
            {
                string label=$"{(nl.Length==2?"CRLF":"LF")}/{(bom?"BOM":"no BOM")}";
                string emptyTemplate="[注入设置]"+nl+starRail+"第三方DLL启用 = true"+nl+"; keep comment"+nl+"[用户设置]"+nl
                    +"# "+starRail+"插件DLL列表 = comment-only"+nl
                    +starRail+"插件DLL列表 = \t"+nl+starRail+"插件启用列表 = \t"+nl
                    +"empty = \t"+nl+"filter = original ; keep inline comment"+nl
                    +"[其他]"+nl+starRail+"插件DLL列表 = must-not-touch"+nl;
                byte[] emptyBytes=Encoding.UTF8.GetBytes(emptyTemplate);
                if(bom)emptyBytes=Encoding.UTF8.GetPreamble().Concat(emptyBytes).ToArray();
                byte[] filled=RocketIntegration.BuildPluginListConfiguration(emptyBytes,starRail,starRailDlls);
                string filledText=Encoding.UTF8.GetString(filled);
                string expectedFilled=emptyTemplate.Replace(starRail+"插件DLL列表 = \t"+nl,starRail+"插件DLL列表 = \t"+starRailPaths+nl)
                    .Replace(starRail+"插件启用列表 = \t"+nl,starRail+"插件启用列表 = \t1|1|1|1"+nl);
                byte[] expectedBytes=Encoding.UTF8.GetBytes(expectedFilled);
                if(bom)expectedBytes=Encoding.UTF8.GetPreamble().Concat(expectedBytes).ToArray();
                Check(filled.SequenceEqual(expectedBytes),label+" 空键同一行写值；换行/空白/注释/无关空值逐字节保留");
                Check(UserValue(filledText,starRail+"插件DLL列表")==starRailPaths
                    &&UserValue(filledText,starRail+"插件启用列表")=="1|1|1|1",label+" 语义读回精确模块路径/顺序/启用数");
                Check(RocketIntegration.BuildPluginListConfiguration(filled,starRail,starRailDlls).SequenceEqual(filled),label+" 空键填充后再次生成幂等");
                string pluginConfig=Path.Combine(root,$"plugin-{nl.Length}-{bom}.ini");File.WriteAllBytes(pluginConfig,emptyBytes);
                var pluginPrepared=RocketIntegration.PreparePluginList(pluginConfig,starRail,starRailDlls);
                Check(pluginPrepared.Success&&pluginPrepared.ConfigPath==Path.GetFullPath(pluginConfig)
                    &&pluginPrepared.BackupPath is { } bp&&File.ReadAllBytes(bp).SequenceEqual(emptyBytes),label+" PreparePluginList 语义回读成功且备份精确");
                Check(File.ReadAllBytes(pluginConfig).SequenceEqual(filled),label+" 落盘与语义校验过的输出一致");
                var pluginAgain=RocketIntegration.PreparePluginList(pluginConfig,starRail,starRailDlls);
                Check(pluginAgain.Success&&pluginAgain.BackupPath is null&&File.ReadAllBytes(pluginConfig).SequenceEqual(filled),label+" 重复 Prepare 不改字节/不新建备份");
                byte[] emptyAgain=RocketIntegration.BuildClearedPluginListConfiguration(filled,starRail);
                Check(emptyAgain.SequenceEqual(emptyBytes),label+" 清空精确还原两个空值并保留注释");
            }
            foreach(string nl in new[]{"\r\n","\n"})
            {
                string partial="[注入设置]"+nl+starRail+"第三方DLL启用 = true"+nl+"[用户设置]"+nl+starRail+"插件DLL列表 = old"+nl+"[其他]"+nl+"keep = yes"+nl;
                string partialOut=Encoding.UTF8.GetString(RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(partial),starRail,starRailDlls));
                Check(UserValue(partialOut,starRail+"插件DLL列表")==starRailPaths&&UserValue(partialOut,starRail+"插件启用列表")=="1|1|1|1"
                    &&partialOut.Contains("[其他]"+nl+"keep = yes"+nl),"单个缺键：插入位置按修改后的行长度计算，不切坏 DLL 值");
                string emptySection="[注入设置]"+nl+starRail+"第三方DLL启用 = true"+nl+"[用户设置]"+nl+"[其他]"+nl+"keep = yes"+nl;
                string sectionOut=Encoding.UTF8.GetString(RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(emptySection),starRail,starRailDlls));
                Check(UserValue(sectionOut,starRail+"插件DLL列表")==starRailPaths&&UserValue(sectionOut,starRail+"插件启用列表")=="1|1|1|1", "空 [用户设置] 插新键不越节");
            }
            string noFinalNewline="[注入设置]\n"+starRail+"第三方DLL启用 = true\n[用户设置]\n"+starRail+"插件DLL列表 = old";
            string noFinalOut=Encoding.UTF8.GetString(RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(noFinalNewline),starRail,starRailDlls));
            Check(UserValue(noFinalOut,starRail+"插件DLL列表")==starRailPaths&&UserValue(noFinalOut,starRail+"插件启用列表")=="1|1|1|1","EOF 无换行且缺启用键时补分隔换行");
            string orphan="[注入设置]\n"+starRail+"第三方DLL启用 = true\n[用户设置]\n"+starRail+"插件DLL列表 = \n"+starRailPaths+"\n"+starRail+"插件启用列表 = \n1|1|1|1\n";
            string orphanConfig=Path.Combine(root,"orphan.ini");File.WriteAllText(orphanConfig,orphan);
            byte[] orphanBytes=File.ReadAllBytes(orphanConfig);
            var orphanResult=RocketIntegration.PreparePluginList(orphanConfig,starRail,starRailDlls);
            Check(!orphanResult.Success&&orphanResult.BackupPath is null&&File.ReadAllBytes(orphanConfig).SequenceEqual(orphanBytes),"历史孤立值格式拒绝准备，不猜删除或写坏原文件");

            // 键不存在时插到 [用户设置] 节末尾，不越到下一个节
            string bare="[注入设置]\r\n鸣潮第三方DLL启用 = true\r\n[原神]\r\n原神包名 = YuanShen.exe\r\n\r\n[用户设置]\r\n软件主题 = 1\r\n\r\n[用户选择]\r\n游戏选择 = 3\r\n";
            string bareOut=Encoding.UTF8.GetString(RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(bare),"鸣潮",[opti]));
            Check(bareOut.Contains("[用户设置]\r\n软件主题 = 1\r\n鸣潮插件DLL列表 = "+Path.GetFullPath(opti)+"\r\n鸣潮插件启用列表 = 1\r\n\r\n[用户选择]"),"缺键时插到 [用户设置] 节末尾，不越节");
            void RejectPlugin(string text,string label)
            {
                bool rejected=false;try{RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(text),"原神",[bridge]);}catch(InvalidDataException){rejected=true;}Check(rejected,label);
            }
            RejectPlugin("[注入设置]\r\n原神第三方DLL启用 = true\r\n[原神]\r\n原神包名 = a\r\n","没有 [用户设置] 节时拒绝写入");
            RejectPlugin("[注入设置]\r\n原神第三方DLL启用 = true\r\n[用户设置]\r\n原神插件DLL列表 = a\r\n原神插件DLL列表 = b\r\n","重复插件列表键拒绝歧义");
            RejectPlugin("[注入设置]\n原神第三方DLL启用 = true\n[用户设置]\n原神插件DLL列表 = a\n原神插件启用列表 = 0\n原神插件启用列表 = 1\n","重复启用列表键拒绝歧义");
            bool missingDll=false;
            try{RocketIntegration.BuildPluginListConfiguration(Encoding.UTF8.GetBytes(bare),"鸣潮",[Path.Combine(root,"nope.dll")]);}catch(FileNotFoundException){missingDll=true;}
            Check(missingDll,"列表里的 DLL 不存在时拒绝写入");

            // 可逆：清空插件列表 = 值留空、无残留、幂等；键不存在时一个字都不改
            byte[] cleared=RocketIntegration.BuildClearedPluginListConfiguration(pluginOut,"原神");
            string clearedText=Encoding.UTF8.GetString(cleared);
            Check(clearedText.Contains("原神插件DLL列表 = \r\n")&&clearedText.Contains("原神插件启用列表 = \r\n"),"清空插件列表=两个键都留空");
            Check(!clearedText.Contains(Path.GetFullPath(opti))&&!clearedText.Contains(Path.GetFullPath(bridge)),"清空后不残留任何 DLL 路径");
            Check(clearedText.Contains("[其他]\r\n原神插件DLL列表 = must-not-touch"),"清空不越过节去动别的节");
            Check(RocketIntegration.BuildClearedPluginListConfiguration(cleared,"原神").SequenceEqual(cleared),"清空幂等");
            Check(Encoding.UTF8.GetString(RocketIntegration.BuildClearedPluginListConfiguration(Encoding.UTF8.GetBytes(bare),"鸣潮"))==bare,"键不存在时清空是空操作（不插空键）");
            Check(!RocketIntegration.ClearPluginList(null,"鸣潮").Success&&!RocketIntegration.ClearPluginList(Path.Combine(root,"no-config.ini"),"鸣潮").Success,"没有 config.ini 时清空报失败而不是假装成功");
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
