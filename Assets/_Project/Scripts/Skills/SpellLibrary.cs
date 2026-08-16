using UnityEngine;

namespace Game.Skills
{
    /// <summary>
    /// 法术编辑界面的候选数据源：保存当前可展示、可拖入有序法术配置的 SpellDefinition 引用数组。
    /// 它只描述“有哪些指令可选”，不保存槽位 UI、拖拽状态或某次施法的 Runtime State。
    /// 单局流程会克隆 Library 及其数组容器，使运行时增删不会直接污染 Project 中的模板资产。
    /// </summary>
    // CreateAssetMenu 在 Unity 的 Assets/Create 菜单中增加创建入口；fileName 是新资产的默认文件名。
    [CreateAssetMenu(menuName = "Game/Skills/Spell Library", fileName = "SpellLibrary")]
    public class SpellLibrary : ScriptableObject
    {
        // Tooltip 只改变 Inspector 的悬停说明，不参与序列化或运行逻辑。
        // 数组元素是共享 SpellDefinition 资产引用；复制数组并不等于深度克隆每个法术资产。
        [Tooltip("调色板里可拖的全部法术")]
        public SpellDefinition[] Available;
    }
}
