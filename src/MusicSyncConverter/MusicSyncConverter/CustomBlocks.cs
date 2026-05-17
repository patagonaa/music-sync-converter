using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks.Dataflow;

namespace MusicSyncConverter
{
    internal static class CustomBlocks
    {
        internal static IPropagatorBlock<TItem, IGrouping<TKey, TItem>> GetGroupByBlock<TItem, TKey>(Func<TItem, TKey> selector, IEqualityComparer<TKey> comparer, CancellationToken cancellationToken = default)
        {
            var source = new BufferBlock<IGrouping<TKey, TItem>>(new DataflowBlockOptions { BoundedCapacity = 8, CancellationToken = cancellationToken });

            var items = new List<TItem>(64);
            TKey? currentKey = default;

            var target = new ActionBlock<TItem>(async x =>
            {
                if (items.Count == 0)
                {
                    currentKey = selector(x) ?? throw new InvalidOperationException("Key must not be null!");
                    items.Add(x);
                }
                else if (comparer.Equals(currentKey, selector(x)))
                {
                    items.Add(x);
                }
                else
                {
                    await source.SendAsync(new Grouping<TKey, TItem>(currentKey!, items.ToArray()));
                    items.Clear();
                    currentKey = selector(x);
                    items.Add(x);
                }
            }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1, BoundedCapacity = 8, CancellationToken = cancellationToken });

            target.Completion.ContinueWith(async x =>
            {
                if (items.Count != 0)
                {
                    await source.SendAsync(new Grouping<TKey, TItem>(currentKey!, items.ToArray()));
                }
                source.Complete();
            });

            return DataflowBlock.Encapsulate(target, source);
        }
    }
}
