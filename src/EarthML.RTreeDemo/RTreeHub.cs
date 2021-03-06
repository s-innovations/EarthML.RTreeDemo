﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json.Linq;

namespace EarthML.RTreeDemo
{
    public class Envelope
    {
        internal Envelope() { }

        public Envelope(double x1, double y1, double x2, double y2)
        {
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
        }

        public double X1 { get; private set; } = int.MaxValue; // 0
        public double Y1 { get; private set; } = int.MaxValue; // 1
        public double X2 { get; private set; } = int.MinValue; // 2
        public double Y2 { get; private set; } = int.MinValue; // 3

        internal double Area { get { return (X2 - X1) * (Y2 - Y1); } }
        internal double Margin { get { return (X2 - X1) + (Y2 - Y1); } }

        internal void Extend(Envelope by)
        {
            X1 = Math.Min(X1, by.X1);
            Y1 = Math.Min(Y1, by.Y1);
            X2 = Math.Max(X2, by.X2);
            Y2 = Math.Max(Y2, by.Y2);
        }

        public override string ToString()
        {
            return String.Format("{0},{1} - {2},{3}", X1, Y1, X2, Y2);
        }

        internal bool Intersects(Envelope b)
        {
            return b.X1 <= X2 && b.Y1 <= Y2 && b.X2 >= X1 && b.Y2 >= Y1;
        }

        internal bool Contains(Envelope b)
        {
            return X1 <= b.X1 && Y1 <= b.Y1 && b.X2 <= X2 && b.Y2 <= Y2;
        }
    }
    public class RTree<T>
    {
        private static readonly EqualityComparer<T> Comparer = EqualityComparer<T>.Default;

        // per-bucket
        private readonly int maxEntries;
        private readonly int minEntries;

        public RTreeNode<T> root;

        public RTree(int maxEntries = 9)
        {
            this.maxEntries = Math.Max(4, maxEntries);
            this.minEntries = (int)Math.Max(2, Math.Ceiling((double)this.maxEntries * 0.4));

            Clear();
        }

        public void Load(IEnumerable<RTreeNode<T>> nnnn)
        {
            var nodes = nnnn.ToList();

            if (nodes.Count < minEntries)
            {
                foreach (var n in nodes) Insert(n);

                return;
            }

            // recursively build the tree with the given data from stratch using OMT algorithm
            var node = BuildOneLevel(nodes, 0, 0);

            if (root.Children.Count == 0)
            {
                // save as is if tree is empty
                root = node;

            }
            else if (root.Height == node.Height)
            {
                // split root if trees have the same height
                SplitRoot(root, node);

            }
            else
            {
                if (root.Height < node.Height)
                {
                    // swap trees if inserted one is bigger
                    var tmpNode = root;
                    root = node;
                    node = tmpNode;
                }

                // insert the small tree into the large tree at appropriate level
                Insert(node, root.Height - node.Height - 1);
            }
        }

        private RTreeNode<T> BuildOneLevel(List<RTreeNode<T>> items, int level, int height)
        {
            RTreeNode<T> node;
            var N = items.Count;
            var M = maxEntries;

            if (N <= M)
            {
                node = new RTreeNode<T> { IsLeaf = true, Height = 1 };
                node.Children.AddRange(items);
            }
            else
            {
                if (level == 0)
                {
                    // target height of the bulk-loaded tree
                    height = (int)Math.Ceiling(Math.Log(N) / Math.Log(M));

                    // target number of root entries to maximize storage utilization
                    M = (int)Math.Ceiling((double)N / Math.Pow(M, height - 1));

                    items.Sort(CompareNodesByMinX);
                }

                node = new RTreeNode<T> { Height = height };

                var N1 = (int)(Math.Ceiling((double)N / M) * Math.Ceiling(Math.Sqrt(M)));
                var N2 = (int)Math.Ceiling((double)N / M);

                var compare = level % 2 == 1
                                ? new Comparison<RTreeNode<T>>(CompareNodesByMinX)
                                : new Comparison<RTreeNode<T>>(CompareNodesByMinY);

                // split the items into M mostly square tiles
                for (var i = 0; i < N; i += N1)
                {
                    var slice = items.GetRange(i, N1);
                    slice.Sort(compare);

                    for (var j = 0; j < slice.Count; j += N2)
                    {
                        // pack each entry recursively
                        var childNode = BuildOneLevel(slice.GetRange(j, N2), level + 1, height - 1);
                        node.Children.Add(childNode);
                    }
                }
            }

            RefreshEnvelope(node);

            return node;
        }

        public IEnumerable<RTreeNode<T>> Search(Envelope envelope)
        {
            var node = root;

            if (!envelope.Intersects(node.Envelope)) return Enumerable.Empty<RTreeNode<T>>();

            var retval = new List<RTreeNode<T>>();
            var nodesToSearch = new Stack<RTreeNode<T>>();

            while (node != null)
            {
                for (var i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    var childEnvelope = child.Envelope;

                    if (envelope.Intersects(childEnvelope))
                    {
                        if (node.IsLeaf) retval.Add(child);
                        else if (envelope.Contains(childEnvelope)) Collect(child, retval);
                        else nodesToSearch.Push(child);
                    }
                }

                node = nodesToSearch.TryPop();
            }

            return retval;
        }

        private static void Collect(RTreeNode<T> node, List<RTreeNode<T>> result)
        {
            var nodesToSearch = new Stack<RTreeNode<T>>();
            while (node != null)
            {
                if (node.IsLeaf) result.AddRange(node.Children);
                else
                {
                    foreach (var n in node.Children)
                        nodesToSearch.Push(n);
                }

                node = nodesToSearch.TryPop();
            }
        }

        public void Clear()
        {
            root = new RTreeNode<T> { IsLeaf = true, Height = 1 };
        }

        public void Insert(RTreeNode<T> item)
        {
            Insert(item, root.Height - 1);
        }

        public void Insert(T data, Envelope bounds)
        {
            Insert(new RTreeNode<T>(data, bounds));
        }

        private void Insert(RTreeNode<T> item, int level)
        {
            var envelope = item.Envelope;
            var insertPath = new List<RTreeNode<T>>();

            // find the best node for accommodating the item, saving all nodes along the path too
            var node = ChooseSubtree(envelope, root, level, insertPath);

            // put the item into the node
            node.Children.Add(item);
            node.Envelope.Extend(envelope);

            // split on node overflow; propagate upwards if necessary
            while (level >= 0)
            {
                if (insertPath[level].Children.Count <= maxEntries) break;

                Split(insertPath, level);
                level--;
            }

            // adjust bboxes along the insertion path
            AdjutsParentBounds(envelope, insertPath, level);
        }

       

        private static double IntersectionArea(Envelope what, Envelope with)
        {
            var minX = Math.Max(what.X1, with.X1);
            var minY = Math.Max(what.Y1, with.Y1);
            var maxX = Math.Min(what.X2, with.X2);
            var maxY = Math.Min(what.Y2, with.Y2);

            return Math.Max(0, maxX - minX) * Math.Max(0, maxY - minY);
        }

        private RTreeNode<T> ChooseSubtree(Envelope bbox, RTreeNode<T> node, int level, List<RTreeNode<T>> path)
        {
            while (true)
            {
                path.Add(node);

                if (node.IsLeaf || path.Count - 1 == level) break;

                var minArea = double.MaxValue;
                var minEnlargement = double.MaxValue;

                RTreeNode<T> targetNode = null;

                for (var i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    var area = child.Envelope.Area;
                    var enlargement = bbox.EnlargedArea(child.Envelope) - area;

                    // choose entry with the least area enlargement
                    if (enlargement < minEnlargement)
                    {
                        minEnlargement = enlargement;
                        minArea = area < minArea ? area : minArea;
                        targetNode = child;

                    }
                    else if (enlargement == minEnlargement)
                    {
                        // otherwise choose one with the smallest area
                        if (area < minArea)
                        {
                            minArea = area;
                            targetNode = child;
                        }
                    }
                }

                Debug.Assert(targetNode != null);
                node = targetNode;
            }

            return node;
        }

        // split overflowed node into two
        private void Split(List<RTreeNode<T>> insertPath, int level)
        {
            var node = insertPath[level];
            var totalCount = node.Children.Count;

            ChooseSplitAxis(node, minEntries, totalCount);

            var newNode = new RTreeNode<T> { Height = node.Height };
            var splitIndex = ChooseSplitIndex(node, minEntries, totalCount);

            newNode.Children.AddRange(node.Children.GetRange(splitIndex, node.Children.Count - splitIndex));
            node.Children.RemoveRange(splitIndex, node.Children.Count - splitIndex);

            if (node.IsLeaf) newNode.IsLeaf = true;

            RefreshEnvelope(node);
            RefreshEnvelope(newNode);

            if (level > 0) insertPath[level - 1].Children.Add(newNode);
            else SplitRoot(node, newNode);
        }

        private void SplitRoot(RTreeNode<T> node, RTreeNode<T> newNode)
        {
            // split root node
            root = new RTreeNode<T>
            {
                Children = { node, newNode },
                Height = node.Height + 1
            };

            RefreshEnvelope(root);
        }

        private int ChooseSplitIndex(RTreeNode<T> node, int minEntries, int totalCount)
        {
            var minOverlap = double.MaxValue;
            var minArea = double.MaxValue;
            int index = 0;

            for (var i = minEntries; i <= totalCount - minEntries; i++)
            {
                var bbox1 = SumChildBounds(node, 0, i);
                var bbox2 = SumChildBounds(node, i, totalCount);

                var overlap = IntersectionArea(bbox1, bbox2);
                var area = bbox1.Area + bbox2.Area;

                // choose distribution with minimum overlap
                if (overlap < minOverlap)
                {
                    minOverlap = overlap;
                    index = i;

                    minArea = area < minArea ? area : minArea;
                }
                else if (overlap == minOverlap)
                {
                    // otherwise choose distribution with minimum area
                    if (area < minArea)
                    {
                        minArea = area;
                        index = i;
                    }
                }
            }

            return index;
        }

        public void Remove(T item, Envelope envelope)
        {
            var node = root;
            var itemEnvelope = envelope;

            var path = new Stack<RTreeNode<T>>();
            var indexes = new Stack<int>();

            var i = 0;
            var goingUp = false;
            RTreeNode<T> parent = null;

            // depth-first iterative tree traversal
            while (node != null || path.Count > 0)
            {
                if (node == null)
                {
                    // go up
                    node = path.TryPop();
                    parent = path.TryPeek();
                    i = indexes.TryPop();

                    goingUp = true;
                }

                if (node != null && node.IsLeaf)
                {
                    // check current node
                    var index = node.Children.FindIndex(n => Comparer.Equals(item, n.Data));

                    if (index != -1)
                    {
                        // item found, remove the item and condense tree upwards
                        node.Children.RemoveAt(index);
                        path.Push(node);
                        CondenseNodes(path.ToArray());

                        return;
                    }
                }

                if (!goingUp && !node.IsLeaf && node.Envelope.Contains(itemEnvelope))
                {
                    // go down
                    path.Push(node);
                    indexes.Push(i);
                    i = 0;
                    parent = node;
                    node = node.Children[0];

                }
                else if (parent != null)
                {
                    i++;
                    if (i == parent.Children.Count)
                    {
                        // end of list; will go up
                        node = null;
                    }
                    else
                    {
                        // go right
                        node = parent.Children[i];
                        goingUp = false;
                    }

                }
                else node = null; // nothing found
            }
        }

        private void CondenseNodes(IList<RTreeNode<T>> path)
        {
            // go through the path, removing empty nodes and updating bboxes
            for (var i = path.Count - 1; i >= 0; i--)
            {
                if (path[i].Children.Count == 0)
                {
                    if (i == 0)
                    {
                        Clear();
                    }
                    else
                    {
                        var siblings = path[i - 1].Children;
                        siblings.Remove(path[i]);
                    }
                }
                else
                {
                    RefreshEnvelope(path[i]);
                }
            }
        }

        // calculate node's bbox from bboxes of its children
        private static void RefreshEnvelope(RTreeNode<T> node)
        {
            node.Envelope = SumChildBounds(node, 0, node.Children.Count);
        }

        private static Envelope SumChildBounds(RTreeNode<T> node, int startIndex, int endIndex)
        {
            var retval = new Envelope();

            for (var i = startIndex; i < endIndex; i++)
            {
                retval.Extend(node.Children[i].Envelope);
            }

            return retval;
        }

        private static void AdjutsParentBounds(Envelope bbox, List<RTreeNode<T>> path, int level)
        {
            // adjust bboxes along the given tree path
            for (var i = level; i >= 0; i--)
            {
                path[i].Envelope.Extend(bbox);
            }
        }

        // sorts node children by the best axis for split
        private static void ChooseSplitAxis(RTreeNode<T> node, int m, int M)
        {
            var xMargin = AllDistMargin(node, m, M, CompareNodesByMinX);
            var yMargin = AllDistMargin(node, m, M, CompareNodesByMinY);

            // if total distributions margin value is minimal for x, sort by minX,
            // otherwise it's already sorted by minY
            if (xMargin < yMargin) node.Children.Sort(CompareNodesByMinX);
        }

        private static int CompareNodesByMinX(RTreeNode<T> a, RTreeNode<T> b) { return a.Envelope.X1.CompareTo(b.Envelope.X1); }
        private static int CompareNodesByMinY(RTreeNode<T> a, RTreeNode<T> b) { return a.Envelope.Y1.CompareTo(b.Envelope.Y1); }

        private static double AllDistMargin(RTreeNode<T> node, int m, int M, Comparison<RTreeNode<T>> compare)
        {
            node.Children.Sort(compare);

            var leftBBox = SumChildBounds(node, 0, m);
            var rightBBox = SumChildBounds(node, M - m, M);
            var margin = leftBBox.Margin + rightBBox.Margin;

            for (var i = m; i < M - m; i++)
            {
                var child = node.Children[i];
                leftBBox.Extend(child.Envelope);
                margin += leftBBox.Margin;
            }

            for (var i = M - m - 1; i >= m; i--)
            {
                var child = node.Children[i];
                rightBBox.Extend(child.Envelope);
                margin += rightBBox.Margin;
            }

            return margin;
        }
    }
    internal static class StackExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T TryPop<T>(this Stack<T> stack)
        {
            return stack.Count == 0 ? default(T) : stack.Pop();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T TryPeek<T>(this Stack<T> stack)
        {
            return stack.Count == 0 ? default(T) : stack.Peek();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double EnlargedArea(this Envelope a, Envelope b)
        {

            return (Math.Max(b.X2, a.X2) - Math.Min(b.X1, a.X1)) * (Math.Max(b.Y2, a.Y2) - Math.Min(b.Y1, a.Y1));
        }
    }
    public class RTreeNode<T>
    {
        private readonly Lazy<List<RTreeNode<T>>> children;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        internal RTreeNode() : this(default(T), new Envelope()) { }

        public RTreeNode(T data, Envelope envelope)
        {
            Data = data;
            Envelope = envelope;
            children = new Lazy<List<RTreeNode<T>>>(() => new List<RTreeNode<T>>(), LazyThreadSafetyMode.None);
        }

        public T Data { get; private set; }
        public Envelope Envelope { get; internal set; }

        internal bool IsLeaf { get; set; }
        internal int Height { get; set; }
        internal List<RTreeNode<T>> Children { get { return children.Value; } }
    }
    public class RTreeHub : Hub
    {
        public static Dictionary<string, RTree<JObject>> Trees = new Dictionary<string, RTree<JObject>>();

        public override Task OnConnected()
        {
            Trees.Add(this.Context.ConnectionId, new RTree<JObject>());

            return base.OnConnected();
        }

        public override Task OnDisconnected(bool stopCalled)
        {
            Trees.Remove(this.Context.ConnectionId);
            return base.OnDisconnected(stopCalled);
        }
        public async Task AddFeature(JObject feature)
        {
            Console.WriteLine(feature.ToString(Newtonsoft.Json.Formatting.Indented));


            double[] x = null; double[] y = null;
            var geom = feature.SelectToken("geometry");

            Unpack(ref x, ref y, geom);

            Trees[Context.ConnectionId].Insert(feature, new Envelope(
             x.Min() / 360.0 +0.5,
             y.Min()  / 180 +0.5,
               x.Max()  / 360.0  +0.5,
               y.Max()  / 180   +0.5));

            var node = Trees[Context.ConnectionId].root;

            Clients.Caller.ClearTree();
            UpdateTree(node);

        }

        private static void Unpack(ref double[] x, ref double[] y, JToken geom)
        {
            switch (geom.SelectToken("type").ToString())
            {
                case "Point":
                    x = geom.SelectToken("coordinates").ToObject<double[]>().Where((p, i) => i % 2 == 0).ToArray();
                    y = geom.SelectToken("coordinates").ToObject<double[]>().Where((p, i) => i % 2 == 1).ToArray();
                    break;
                case "Polygon":
                    x = geom.SelectToken("coordinates").ToObject<double[][][]>().SelectMany(p => p.SelectMany(p1 => p1)).Where((p, i) => i % 2 == 0).ToArray();
                    y = geom.SelectToken("coordinates").ToObject<double[][][]>().SelectMany(p => p.SelectMany(p1 => p1)).Where((p, i) => i % 2 == 1).ToArray();
                    break;

                case "GeometryCollection":

                    break;
            }
        }

        private void UpdateTree(RTreeNode<JObject> node)
        {
            if (node.Data == null)
            {
                Clients.Caller.UpdateTree(new JObject(
                    new JProperty("id", node.Id),
                     new JProperty("height", node.Height),
                    new JProperty("geometry",                    
                    new JObject(
                        new JProperty("type", "Polygon"),
                        new JProperty("coordinates", JToken.FromObject(new double[][][] {
                        new double[][]
                        {
                            new double[] {((node.Envelope.X1)-0.5)*360, (node.Envelope.Y1-0.5)*180},
                            new double[] {((node.Envelope.X1)-0.5) * 360, (node.Envelope.Y2-0.5)*180 },
                            new double[] {((node.Envelope.X2) - 0.5)*360, (node.Envelope.Y2-0.5)*180 },
                            new double[] {((node.Envelope.X2) - 0.5)*360, (node.Envelope.Y1-0.5)*180 },
                            new double[] {((node.Envelope.X1) - 0.5)*360, (node.Envelope.Y1 - 0.5) * 180 }


                        }
                        })

                    ))
                    )));
            }

            foreach(var child in node.Children)
            {
                UpdateTree(child);
            }
        }
    }
}
