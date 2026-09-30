#!/bin/bash
# 诊断 ZZZ NR owner 生命周期

LOG="D:/APPS/HoYoShadeHub/OptiScaler/mfg-ada/mfg-ada-0.1.5/OptiScaler.log"

if [ ! -f "$LOG" ]; then
    echo "日志文件不存在: $LOG"
    exit 1
fi

echo "=== ZZZ NR 诊断报告 ==="
echo "日志时间范围:"
grep "\[I\]" "$LOG" | head -1
grep "\[I\]" "$LOG" | tail -1
echo ""

echo "=== NR Dispatch 调用（owner 激活） ==="
count=$(grep -c "NR Dispatch:" "$LOG")
echo "总次数: $count"
if [ $count -gt 0 ]; then
    echo "前 5 次:"
    grep "NR Dispatch:" "$LOG" | head -5
fi
echo ""

echo "=== NR owner retire 事件 ==="
grep "retire owner" "$LOG"
echo ""

echo "=== activeNrOwner 为 nullptr 的情况 ==="
count=$(grep -c "activeNrOwner is nullptr" "$LOG")
echo "总次数: $count"
if [ $count -gt 0 ]; then
    echo "前 5 次:"
    grep "activeNrOwner is nullptr" "$LOG" | head -5
fi
echo ""

echo "=== NR applied picture（真正运行的证据） ==="
count=$(grep -c "NR bridge: applied picture" "$LOG")
echo "总次数: $count"
if [ $count -gt 0 ]; then
    grep "NR bridge: applied picture" "$LOG" | head -3
else
    echo "未找到 NR applied picture，表示 NR 从未真正运行"
fi
echo ""

echo "=== FGPresent 调用统计 ==="
count=$(grep -c "FGHooks::FGPresent Result:" "$LOG")
echo "总次数: $count"
echo ""

echo "=== 总体结论 ==="
if [ $(grep -c "NR Dispatch:" "$LOG") -gt 0 ] && [ $(grep -c "retire owner" "$LOG") -gt 0 ]; then
    if [ $(grep -c "NR bridge: applied picture" "$LOG") -eq 0 ]; then
        echo "❌ 问题确认："
        echo "   - NR owner 被创建过并激活"
        echo "   - NR owner 被回收"
        echo "   - 但 NR 从未真正运行 ApplyFinished (no applied picture)"
        echo "   - 可能原因: activeNrOwner 在 ApplyToFinishedPicture 被调用时已是 nullptr"
    fi
fi
