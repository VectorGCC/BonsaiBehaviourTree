﻿using System;
using System.Collections.Generic;
using System.Linq;
using Bonsai.Standard;
using UnityEditor;
using UnityEngine;

namespace Bonsai.Core
{
  [CreateAssetMenu(fileName = "BonsaiBT", menuName = "Bonsai/Behaviour Tree")]
  public class BehaviourTree : ScriptableObject
  {
    private BehaviourIterator mainIterator;

    // Only conditional decorator nodes can have observer properties.
    private List<ConditionalAbort> observerAborts;

    // Store references to the parallel nodes;
    private Parallel[] parallelNodes;

    /// <summary>
    /// Nodes that are allowed to update on tree tick.
    /// </summary>
    private BehaviourNode[] treeTickNodes;

    [SerializeField, HideInInspector]
    private BehaviourNode _root;

    private bool isTreeInitialized = false;

    /// <summary>
    /// The game object binded to the tree.
    /// This is assigned at runtime when the tree instance starts.
    /// </summary>
    public GameObject actor;

    /// <summary>
    /// Gets and sets the tree root.
    /// </summary>
    public BehaviourNode Root
    {
      get { return _root; }
      set
      {
        // NOTE:
        // Everytime we set the root, Start()
        // must be called again in order to preprocess the tree.
        isTreeInitialized = false;

        if (value == null)
        {
          Debug.LogWarning("Cannot initialize with null node");
          return;
        }

        // Setup root.
        if (value.Parent == null)
        {
          _root = value;
        }

        else
        {
          Debug.LogWarning("Cannot set parented node as tree root.");
        }
      }
    }

    [SerializeField, HideInInspector]
    private Blackboard _blackboard;

    public Blackboard Blackboard
    {
      get { return _blackboard; }
    }

    [SerializeField, HideInInspector]
    internal List<BehaviourNode> allNodes = new List<BehaviourNode>();

    public void SetBlackboard(Blackboard bb)
    {
      _blackboard = bb;
    }

    /// <summary>
    /// Preprocesses and starts the tree.
    /// </summary>
    /// <param name="root"></param>
    public void Start()
    {
      if (_root == null)
      {
        Debug.LogWarning("Cannot start tree with a null root.");
        return;
      }

      PreProcess();

      foreach (BehaviourNode node in allNodes)
      {
        node.OnStart();
      }

      mainIterator.Traverse(_root);
      isTreeInitialized = true;
    }

    public void Update()
    {
      if (isTreeInitialized && mainIterator.IsRunning)
      {

        if (treeTickNodes.Length != 0)
        {
          NodeTreeTick();
        }

        if (observerAborts.Count != 0)
        {
          TickObservers();
        }

        mainIterator.Update();
      }
    }

    /// <summary>
    /// Processes the tree to calculate certain properties like node priorities,
    /// caching observers, and syncinc parallel iterators.
    /// The root must be set.
    /// </summary>
    private void PreProcess()
    {
      if (_root == null)
      {
        Debug.Log("The tree must have a valid root in order to be pre-processed");
        return;
      }

      CalculateTreeOrders();

      mainIterator = new BehaviourIterator(this, 0);

      // Setup a new list for the observer nodes.
      observerAborts = new List<ConditionalAbort>();

      CacheObservers();
      CacheTreeTickNodes();
      SyncIterators();
    }

    private void CacheObservers()
    {
      observerAborts.Clear();
      observerAborts.AddRange(
        GetNodes<ConditionalAbort>()
        .Where(node => node.abortType != AbortType.None));
    }

    private void CacheTreeTickNodes()
    {
      treeTickNodes = allNodes.Where(node => node.CanTickOnTree()).ToArray();
    }

    private void SyncIterators()
    {
      SyncParallelIterators();

      _root._iterator = mainIterator;

      BehaviourIterator itr = mainIterator;
      var parallelRoots = new Stack<BehaviourNode>();

      // This function handles assigning the iterator and skipping nodes.
      // The parallel root uses the same iterator as its parent, but the children
      // of the parallel node use their own iterator.
      Func<BehaviourNode, bool> skipAndAssign = (node) =>
      {
        node._iterator = itr;

        bool isParallel = node as Parallel != null;

        if (isParallel)
        {
          parallelRoots.Push(node);
        }

        return isParallel;
      };

      // Assign the main iterator to nodes not under any parallel nodes.
      TreeIterator<BehaviourNode>.Traverse(_root, delegate { }, skipAndAssign);

      while (parallelRoots.Count != 0)
      {
        BehaviourNode parallel = parallelRoots.Pop();

        // Do passes for each child, using the sub iterator associated with that child.
        for (int i = 0; i < parallel.ChildCount(); ++i)
        {
          itr = (parallel as Parallel).GetIterator(i);
          TreeIterator<BehaviourNode>.Traverse(parallel.GetChildAt(i), delegate { }, skipAndAssign);
        }
      }
    }

    private void SyncParallelIterators()
    {
      parallelNodes = GetNodes<Parallel>().ToArray();

      // Cache the parallel nodes and syn their iterators.
      foreach (Parallel p in parallelNodes)
      {
        p.SyncSubIterators();
      }
    }

    public void Interrupt(BehaviourNode subroot, bool bFullInterrupt = false)
    {
      // Interrupt this subtree.
      subroot.Iterator.StepBackInterrupt(subroot, bFullInterrupt);

      // Look for parallel nodes under the subroot.
      // Since the parallel count is usually small, we 
      // can just do a linear iteration to interrupt multiple
      // parallel nodes.
      for (int pIndex = 0; pIndex < parallelNodes.Length; ++pIndex)
      {
        Parallel p = parallelNodes[pIndex];

        if (IsUnderSubtree(subroot, p))
        {
          for (int itrIndex = 0; itrIndex < p.ChildCount(); ++itrIndex)
          {
            BehaviourIterator itr = p.GetIterator(itrIndex);

            // Only interrupt running iterators.
            if (itr.IsRunning)
            {
              // Get the child of the parallel node, and interrupt the child subtree.
              int childIndex = itr.FirstInTraversal;
              BehaviourNode firstNode = allNodes[childIndex];

              itr.StepBackInterrupt(firstNode.Parent, bFullInterrupt);
            }
          }
        }
      }
    }

    /// <summary>
    /// Computes the pre and post orders of all nodes.
    /// </summary>
    public void CalculateTreeOrders()
    {
      ResetOrderIndices();

      int orderCounter = 0;
      TreeIterator<BehaviourNode>.Traverse(
        _root,
        node => node.preOrderIndex = orderCounter++);

      orderCounter = 0;
      TreeIterator<BehaviourNode>.Traverse(
        _root,
        node => node.postOrderIndex = orderCounter++,
        Traversal.PostOrder);

      TreeIterator<BehaviourNode>.Traverse(
        _root,
        (node, itr) =>
        {
          node.levelOrder = itr.CurrentLevel;
          Height = itr.CurrentLevel;
        },
        Traversal.LevelOrder);
    }

    /// <summary>
    /// Gets the nodes of type T.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public IEnumerable<T> GetNodes<T>() where T : BehaviourNode
    {
      return allNodes.Select(node => node as T).Where(casted => casted != null);
    }

    // Note on multiple aborts:
    // If there are multiple satisfied aborts, then
    // the tree picks the highest order abort (left most).
    private void TickObservers()
    {
      for (int i = 0; i < observerAborts.Count; ++i)
      {
        ConditionalAbort node = observerAborts[i];

        // The iterator must be running since aborts can only occur under 
        // actively running subtrees.
        if (!node.Iterator.IsRunning)
        {
          continue;
        }

        // If the condition is true then apply an abort.
        if (node.IsAbortSatisfied())
        {
          node.Iterator.OnAbort(node);
        }
      }
    }

    private void NodeTreeTick()
    {
      for (int i = 0; i < treeTickNodes.Length; i++)
      {
        treeTickNodes[i].OnTreeTick();
      }
    }

    /// <summary>
    /// Tests if the order of a is lower than b.
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public static bool IsLowerOrder(int orderA, int orderB)
    {
      // 1 is the highest priority.
      // Greater numbers means lower priority.
      return orderA > orderB;
    }

    /// <summary>
    /// Tests if the order of a is higher than b.
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public static bool IsHigherOrder(int orderA, int orderB)
    {
      return orderA < orderB;
    }

    /// <summary>
    /// Tests if node is under the root tree.
    /// </summary>
    /// <param name="root"></param>
    /// <param name="node"></param>
    /// <returns></returns>
    public static bool IsUnderSubtree(BehaviourNode root, BehaviourNode node)
    {
      // Assume that this is the root of the tree root.
      // This would happen when checking IsUnderSubtree(node.parent, other)
      if (root == null)
      {
        return true;
      }

      return root.PostOrderIndex > node.PostOrderIndex && root.PreOrderIndex < node.PreOrderIndex;
    }

    public bool IsRunning()
    {
      return mainIterator != null && mainIterator.IsRunning;
    }

    public BehaviourNode.Status LastStatus()
    {
      return mainIterator.LastStatusReturned;
    }

    public int Height { get; private set; } = 0;

    /// <summary>
    /// Gets the instantiated copy version of a behaviour node from its original version.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="tree">The instantiated tree.</param>
    /// <param name="original">The node in the original tree.</param>
    /// <returns></returns>
    public static T GetInstanceVersion<T>(BehaviourTree tree, BehaviourNode original) where T : BehaviourNode
    {
      return GetInstanceVersion(tree, original) as T;
    }

    public static BehaviourNode GetInstanceVersion(BehaviourTree tree, BehaviourNode original)
    {
      int index = original.preOrderIndex;
      return tree.allNodes[index];
    }

    /// <summary>
    /// Deep copies the tree.
    /// Make sure that the original behaviour tree has its pre-orders calculated.
    /// </summary>
    /// <param name="originalBT"></param>
    /// <returns></returns>
    public static BehaviourTree Clone(BehaviourTree originalBT)
    {
      var cloneBt = Instantiate(originalBT);

      if (originalBT._blackboard)
      {
        cloneBt._blackboard = Instantiate(originalBT._blackboard);
      }

      cloneBt.allNodes.Clear();

      Action<BehaviourNode> copier = (originalNode) =>
      {
        var nodeCopy = Instantiate(originalNode);

        // Linke the root copy.
        if (originalBT.Root == originalNode)
        {
          cloneBt.Root = nodeCopy;
        }

        // Nodes will be added in pre-order.
        nodeCopy.ClearTree();
        nodeCopy.Tree = cloneBt;
      };

      // Traversing in tree order will make sure that the runtime tree has its nodes properly sorted
      // in pre-order and will also make sure that dangling nodes are left out (unconnected nodes from the editor).
      TreeIterator<BehaviourNode>.Traverse(originalBT.Root, copier);

      // At this point the clone BT has its children in pre order order
      // and the original BT has pre-order indices calculated for each node.
      //
      // RELINK children and parent associations of the cloned nodes.
      // The clone node count is <= original node count because the editor may have dangling nodes.
      int maxCloneNodeCount = cloneBt.allNodes.Count;
      for (int i = 0; i < maxCloneNodeCount; ++i)
      {

        BehaviourNode originalNode = originalBT.allNodes[i];
        BehaviourNode originalParent = originalNode.Parent;

        if (originalParent)
        {

          BehaviourNode copyNode = GetInstanceVersion(cloneBt, originalNode);
          BehaviourNode copyParent = GetInstanceVersion(cloneBt, originalParent);

          copyParent.ForceSetChild(copyNode);
        }
      }

      for (int i = 0; i < maxCloneNodeCount; ++i)
      {
        cloneBt.allNodes[i].OnCopy();
      }

      return cloneBt;
    }

    /// <summary>
    /// Sorts the nodes in pre order.
    /// </summary>
    public void SortNodes()
    {
      CalculateTreeOrders();

      // Moves back the dangling nodes to the end of the list and then
      // sorts the nodes by pre-order.
      allNodes = allNodes
          .OrderBy(node => node.preOrderIndex == BehaviourNode.kInvalidOrder)
          .ThenBy(node => node.preOrderIndex)
          .ToList();
    }

    /// <summary>
    /// Gets the node at the specified pre-order index.
    /// </summary>
    /// <param name="preOrderIndex"></param>
    /// <returns></returns>
    public BehaviourNode GetNode(int preOrderIndex)
    {
      return allNodes[preOrderIndex];
    }

    public IEnumerable<BehaviourNode> AllNodes
    {
      get { return allNodes; }
    }

    /// <summary>
    /// Resets the pre and post order indices.
    /// </summary>
    public void ResetOrderIndices()
    {
      foreach (BehaviourNode b in allNodes)
      {
        b.preOrderIndex = BehaviourNode.kInvalidOrder;
        b.postOrderIndex = BehaviourNode.kInvalidOrder;
        b.levelOrder = BehaviourNode.kInvalidOrder;
      }
    }

    /// <summary>
    /// Clear tree structure references.
    /// <list type="bullet">
    /// <item>Root</item>
    /// <item>References to parent Tree</item>
    /// <item>Parent-Child connections</item>
    /// <item>Internal Nodes List</item>
    /// </list>
    /// </summary>
    public void ClearStructure()
    {
      foreach (BehaviourNode node in allNodes)
      {
        node.ClearChildren();
        node.ClearTree();
      }

      allNodes.Clear();

      _root = null;
    }

#if UNITY_EDITOR

    [ContextMenu("Add Blackboard")]
    void AddBlackboardAsset()
    {
      if (_blackboard == null && !EditorApplication.isPlaying)
      {
        _blackboard = CreateInstance<Blackboard>();
        _blackboard.hideFlags = HideFlags.HideInHierarchy;
        AssetDatabase.AddObjectToAsset(_blackboard, this);
      }
    }

    [HideInInspector]
    public Vector2 panPosition = Vector2.zero;

    [HideInInspector]
    public Vector2 zoomPosition = Vector2.one;

#endif

  }
}