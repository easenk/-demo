using System.Runtime.CompilerServices;

// 测试程序集可见 internal（批次B1 拍板）：三不变式的违规场景必须手写合成事件流——
// 真实逻辑构造不出不变式违反（那正是要证明的"构造上不可能"），而 BattleEventStream.Add
// 与 PlannedTimeline 的内部构造器按零分配纪律不开公口，internal 可见是唯一干净入口。
// 玩家构建不含测试程序集，生产面零暴露。
[assembly: InternalsVisibleTo("AutoChess.Tests")]
