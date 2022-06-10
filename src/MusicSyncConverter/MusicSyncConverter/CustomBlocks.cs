using System;
using System.Collections.Generic;
using System.Threading.Tasks.Dataflow;

namespace MusicSyncConverter
{
    internal static class CustomBlocks
    {
        internal static IPropagatorBlock<TItem, TItem[]> GetGroupByBlock<TItem, TKey>(Func<TItem, TKey> selector, IEqualityComparer<TKey> comparer)
        {
            var source = new BufferBlock<TItem[]>(new DataflowBlockOptions { BoundedCapacity = 8 });

            var items = new List<TItem>(64);
            TKey? currentKey = default;

            var target = new ActionBlock<TItem>(async x =>
            {
                if (items.Count == 0)
                {
                    currentKey = selector(x);
                    items.Add(x);
                }
                else if (comparer.Equals(currentKey, selector(x)))
                {
                    items.Add(x);
                }
                else
                {
                    await source.SendAsync(items.ToArray());
                    items.Clear();
                    currentKey = selector(x);
                    items.Add(x);
                }
            }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 8 });

            target.Completion.ContinueWith(async x =>
            {
                await source.SendAsync(items.ToArray());
                source.Complete();
            });

            return DataflowBlock.Encapsulate(target, source);
        }
    }
}
