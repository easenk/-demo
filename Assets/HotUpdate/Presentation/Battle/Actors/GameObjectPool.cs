using System.Collections.Generic;
using UnityEngine;

namespace AutoChess.Presentation
{
    /// <summary>
    /// 通用实例池（批次4C，补参考工程欠账：单位/飘字全走池，4D 弹体复用同款）。
    /// 形态最简：栈 + 收纳根物体。不挂负载——归还者自己清状态（本工程一切姿势按 playhead
    /// 参数化，actor 状态天然随 BeginBattle 重建，池只管 GameObject 的生死与显隐）。
    /// </summary>
    public sealed class GameObjectPool
    {
        private readonly Stack<GameObject> _stack = new Stack<GameObject>(32);
        private readonly Transform _root;

        public GameObjectPool(string name)
        {
            var go = new GameObject($"Pool_{name}");
            go.SetActive(false);          // 收纳根常隐：池里的闲置件不参与渲染与更新
            _root = go.transform;
        }

        /// <summary>取一件（池空则新建）。出池后为世界层级、已激活——外形与子件由调用方装配。</summary>
        public GameObject Get(string objectName)
        {
            GameObject go = _stack.Count > 0 ? _stack.Pop() : new GameObject(objectName);
            go.name = objectName;
            go.transform.SetParent(null, false);
            go.SetActive(true);
            return go;
        }

        /// <summary>还一件（归藏：失活 + 挂回收纳根下）。</summary>
        public void Release(GameObject go)
        {
            if (go == null) return;
            go.SetActive(false);
            go.transform.SetParent(_root, false);
            _stack.Push(go);
        }

        /// <summary>清空池（场景销毁时用——池里的件是场景物体，不销毁会漏）。</summary>
        public void Dispose()
        {
            while (_stack.Count > 0)
            {
                GameObject go = _stack.Pop();
                if (go != null) Object.Destroy(go);
            }
            if (_root != null) Object.Destroy(_root.gameObject);
        }
    }
}
