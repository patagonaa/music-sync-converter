using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace MusicSyncConverter
{
    internal class Grouping<TKey, TElement> : IGrouping<TKey, TElement>
    {
        private readonly TKey _key;
        private readonly IEnumerable<TElement> _values;

        public Grouping(TKey key, IEnumerable<TElement> values)
        {
            ArgumentNullException.ThrowIfNull(values, nameof(values));
            _key = key;
            _values = values;
        }

        public TKey Key
        {
            get { return _key; }
        }

        public IEnumerator<TElement> GetEnumerator()
        {
            return _values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
