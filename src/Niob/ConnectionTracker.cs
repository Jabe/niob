using System;
using System.Collections.Generic;
using System.Linq;

namespace Niob
{
    public class ConnectionTracker
    {
        public ConnectionTracker(TimeSpan purgeAfter)
        {
            Timestamps = new Dictionary<string, LinkedList<DateTime>>();

            PurgeAfter = purgeAfter;
        }

        protected Dictionary<string, LinkedList<DateTime>> Timestamps { get; private set; }

        public TimeSpan PurgeAfter { get; private set; }

        public virtual void Track(string id)
        {
            LinkedList<DateTime> list = GetList(id);

            lock (list)
            {
                list.AddLast(DateTime.UtcNow);
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

        public virtual int GetCount(string id)
        {
            LinkedList<DateTime> list = GetList(id);

            lock (list)
            {
                Purge(list);

                return list.Count;
            }
        }

        public void Purge()
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
    }
}