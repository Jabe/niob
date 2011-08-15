using System;
using System.Collections.Generic;
using System.Linq;

namespace Niob
{
    /// <summary>
    /// A counter indexed by an id. Individual entries will be purged after a specified time.
    /// </summary>
    public class TimedCounter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TimedCounter"/> class.
        /// </summary>
        /// <param name="purgeAfter">The time after which the individual items will be purged.</param>
        public TimedCounter(TimeSpan purgeAfter)
        {
            Timestamps = new Dictionary<string, LinkedList<DateTime>>();

            PurgeAfter = purgeAfter;
        }

        /// <summary>
        /// Gets the timestamps.
        /// </summary>
        /// <value>The timestamps.</value>
        protected Dictionary<string, LinkedList<DateTime>> Timestamps { get; private set; }

        /// <summary>
        /// Gets the time after which the individual items will be purged.
        /// </summary>
        /// <value>The purge after.</value>
        public TimeSpan PurgeAfter { get; private set; }

        /// <summary>
        /// Increments the count of the specified id. This method is thread-safe.
        /// </summary>
        /// <param name="id">The id.</param>
        public virtual void Increment(string id)
        {
            LinkedList<DateTime> list = GetList(id);

            lock (list)
            {
                list.AddLast(DateTime.UtcNow);
            }
        }

        /// <summary>
        /// Gets the count for the specified id. This method is thread-safe.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns></returns>
        public virtual int GetCount(string id)
        {
            LinkedList<DateTime> list = GetList(id);

            lock (list)
            {
                Purge(list);

                return list.Count;
            }
        }

        /// <summary>
        /// Purges all expired entries. This method is thread-safe.
        /// </summary>
        public virtual void Purge()
        {
            lock (Timestamps)
            {
                var ids = Timestamps.Keys.ToArray();

                foreach (var id in ids)
                {
                    LinkedList<DateTime> list = GetList(id);

                    lock (list)
                    {
                        Purge(list);

                        if (list.Count == 0)
                        {
                            Timestamps.Remove(id);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Purges all expired entries of the specified id. This method is thread-safe.
        /// </summary>
        /// <param name="id">The id.</param>
        public virtual void Purge(string id)
        {
            LinkedList<DateTime> list = GetList(id);

            lock (list)
            {
                Purge(list);
            }
        }

        /// <summary>
        /// Purges all expired entries of the specified list. This method is thread-safe.
        /// </summary>
        /// <param name="list">The list.</param>
        protected virtual void Purge(LinkedList<DateTime> list)
        {
            if (list.First == null)
                return;

            DateTime mark = DateTime.UtcNow;
            DateTime purge = mark - PurgeAfter;

            LinkedListNode<DateTime> node = list.First;

            while (node != null)
            {
                LinkedListNode<DateTime> cur = node;

                node = node.Next;

                if (cur.Value < purge)
                {
                    list.Remove(cur);
                }
            }
        }

        private LinkedList<DateTime> GetList(string id)
        {
            LinkedList<DateTime> list;

            lock (Timestamps)
            {
                if (!Timestamps.TryGetValue(id, out list))
                {
                    list = new LinkedList<DateTime>();
                    Timestamps[id] = list;
                }
            }

            return list;
        }
    }
}