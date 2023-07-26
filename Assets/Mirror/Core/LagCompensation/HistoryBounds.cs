// HistoryBounds keeps a bounding box of all the object's bounds in the past N seconds.
// useful to decide which objects to rollback, instead of rolling back all of them.
// https://www.youtube.com/watch?v=zrIY0eIyqmI (37:00)
// standalone C# implementation to be engine (and language) agnostic.

using System.Collections.Generic;
using UnityEngine;

namespace Mirror
{
    public class HistoryBounds
    {
        // FakeByte: gather bounds in smaller buckets.
        // for example, bucket(t0,t1,t2), bucket(t3,t4,t5), ...
        // instead of removing old bounds t0, t1, ...
        // we remove a whole bucket every 3 times: bucket(t0,t1,t2)
        // and when building total bounds, we encapsulate a few larger buckets
        // instead of many smaller bounds.
        //
        // => a bucket is encapsulate(bounds0, bounds1, bounds2) so we don't
        //    need a custom struct, simply reuse bounds but remember that each
        //    entry includes N timestamps.
        //
        // => note that simply reducing capture interval is _not_ the same.
        //    we want to capture in detail in case players run in zig-zag.
        //    but still grow larger buckets internally.
        readonly int boundsPerBucket;
        readonly Queue<Bounds> fullBuckets;

        Bounds? currentBucket;
        int currentBucketSize; // 0..boundsPerBucket

        // history limit. oldest bounds will be removed.
        public readonly int boundsLimit;
        readonly int bucketLimit;

        // amount of total bounds, including bounds in full buckets + current
        public int boundsCount { get; private set; }

        // total bounds encapsulating all of the bounds history
        public Bounds total;

        public HistoryBounds(int boundsLimit, int boundsPerBucket)
        {
            // initialize queue with maximum capacity to avoid runtime resizing
            this.boundsPerBucket = boundsPerBucket;
            this.boundsLimit = boundsLimit;
            this.bucketLimit = (boundsLimit / boundsPerBucket);

            // capacity +1 because it makes the code easier if we insert first, and then remove.
            fullBuckets = new Queue<Bounds>(bucketLimit + 1);
        }

        // insert new bounds into history. calculates new total bounds.
        // Queue.Dequeue() always has the oldest bounds.
        public void Insert(Bounds bounds)
        {
            // initialize 'total' if not initialized yet.
            // we don't want to call (0,0).Encapsulate(bounds).
            if (boundsCount == 0)
            {
                total = bounds;
            }

            // add to current bucket:
            // either initialize new one, or encapsulate into existing one
            if (currentBucket == null)
            {
                currentBucket = bounds;
            }
            else
            {
                currentBucket.Value.Encapsulate(bounds);
            }

            // current bucket has one more bounds.
            // total bounds increased as well.
            currentBucketSize += 1;
            boundsCount += 1;

            // always encapsulate into total immediately.
            // this is free.
            total.Encapsulate(bounds);

            // current bucket full?
            if (currentBucketSize == boundsPerBucket)
            {
                // move it to full buckets
                fullBuckets.Enqueue(currentBucket.Value);
                currentBucket = null;
                currentBucketSize = 0;

                // full bucket capacity reached?
                if (fullBuckets.Count > bucketLimit)
                {
                    // remove oldest bucket
                    fullBuckets.Dequeue();
                    boundsCount -= boundsPerBucket;

                    // recompute total bounds
                    // instead of iterating N buckets, we iterate N / boundsPerBucket buckets.
                    // TODO technically we could reuse 'currentBucket' before clearing instead of encapsulating again
                    total = bounds;
                    foreach (Bounds bucket in fullBuckets)
                        total.Encapsulate(bucket);
                }
            }
        }

        public void Reset()
        {
            fullBuckets.Clear();
            currentBucket = null;
            currentBucketSize = 0;
            boundsCount = 0;
            total = new Bounds();
        }
    }
}
