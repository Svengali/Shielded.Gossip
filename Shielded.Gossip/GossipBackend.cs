﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Shielded.Gossip
{
    /// <summary>
    /// A backend supporting a key/value store which is distributed using a simple gossip protocol
    /// implementation. Use it in ordinary <see cref="Shield"/> transactions.
    /// Values should be CRDTs, implementing <see cref="IMergeable{T}"/>, or you can use the
    /// <see cref="Multiple{T}"/> and <see cref="VecVersioned{T}"/> wrappers to make them a CRDT.
    /// </summary>
    public class GossipBackend : IGossipBackend, IDisposable
    {
        private readonly ShieldedDictNc<string, MessageItem> _local = new ShieldedDictNc<string, MessageItem>();
        private readonly ReverseTimeIndex _freshIndex;
        private readonly ShieldedLocal<Dictionary<string, MessageItem>> _toMail = new ShieldedLocal<Dictionary<string, MessageItem>>();

        private readonly Timer _gossipTimer;
        private readonly Timer _deletableTimer;

        public readonly ITransport Transport;
        public readonly GossipConfiguration Configuration;

        /// <summary>
        /// Event raised when an unexpected error occurs on one of the background tasks of the backend.
        /// </summary>
        public event EventHandler<GossipBackendException> Error;

        private readonly object _owner;

        internal void RaiseError(GossipBackendException ex) => Error?.Invoke(_owner, ex);

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="transport">The message transport to use. The backend will dispose it when it gets disposed.</param>
        /// <param name="configuration">The configuration.</param>
        public GossipBackend(ITransport transport, GossipConfiguration configuration)
            : this(transport, configuration, null) { }

        internal GossipBackend(ITransport transport, GossipConfiguration configuration, object owner)
        {
            _owner = owner ?? this;
            Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _freshIndex = new ReverseTimeIndex(GetItemDirect);
            _gossipTimer = new Timer(_ => SpreadRumors(), null, Configuration.GossipInterval, Configuration.GossipInterval);
            _deletableTimer = new Timer(GetDeletableTimerMethod(), null, Configuration.CleanUpInterval, Configuration.CleanUpInterval);
            _applyMethods = new ApplyMethods((key, fi) => SetInternal(key, fi));
            Transport.MessageHandler = Transport_MessageHandler;
        }

        private TimerCallback GetDeletableTimerMethod()
        {
            var lockObj = new object();
            return _ =>
            {
                bool lockTaken = false;
                try
                {
                    Monitor.TryEnter(lockObj, ref lockTaken);
                    if (!lockTaken)
                        return;
                    var currTickCount = Environment.TickCount;
                    var toRemove = Shield.InTransaction(() =>
                        _freshIndex
                            .Where(i => i.Item.RemovableSince.HasValue
                                ? unchecked(currTickCount - i.Item.RemovableSince.Value) > Configuration.RemovableItemLingerMs
                                : i.Item.ExpiresInMs <= 0)
                            .Select(i => i.Item)
                            .ToArray());
                    Shield.InTransaction(() =>
                    {
                        foreach (var item in toRemove)
                            if (_local.TryGetValue(item.Key, out var mi) && mi == item)
                            {
                                if (item.RemovableSince.HasValue)
                                    _local.Remove(item.Key);
                                else
                                    Expire(item);
                            }
                    });
                }
                catch (Exception ex)
                {
                    RaiseError(new GossipBackendException("Unexpected error on deletable timer task.", ex));
                }
                finally
                {
                    if (lockTaken)
                        Monitor.Exit(lockObj);
                }
            };
        }

        private void Expire(MessageItem item)
        {
            var hash = GetHash(item.Key, (IHasVersionBytes)item.Value);
            var newItem = new MessageItem
            {
                Key = item.Key,
                Data = item.Data,
                Expired = true,
            };
            _local[item.Key] = newItem;
            _freshIndex.Append(newItem, hash);
        }

        private void DoDirectMail(MessageItem[] items)
        {
            if (Configuration.DirectMail == DirectMailType.Off || items.Length == 0)
                return;
            var package = new DirectMail { Items = items };
            if (Configuration.DirectMail == DirectMailType.Always)
            {
                Transport.Broadcast(package);
            }
            else
            {
                foreach (var server in Transport.Servers)
                    SendMail(server, package);
            }
        }

        private void SendMail(string server, DirectMail package)
        {
            if (Configuration.DirectMail == DirectMailType.Always ||
                Configuration.DirectMail == DirectMailType.GossipSupressed && !IsGossipActive(server))
                Transport.Send(server, package, false);
            else if (Configuration.DirectMail == DirectMailType.StartGossip)
                StartGossip(server);
        }

        private enum MessageType
        {
            Start,
            Reply,
            End
        }

        private class GossipState
        {
            public readonly int? LastReceivedMsgId;
            public readonly int LastSentMsgId;
            public ReverseTimeIndex.Enumerator LastWindowStart { get; private set; }
            public readonly int LastPackageSize;
            public readonly MessageType LastSentMsgType;
            // used only when LastSentMsgType == MessageType.Start
            public readonly int? PreviousSentEndMsgId;

            public readonly int CreationTickCount = Environment.TickCount;

            public GossipState(int? lastReceivedMsgId, int lastSentMsgId, ReverseTimeIndex.Enumerator lastWindowStart,
                int lastPackageSize, MessageType lastSentMsgType, int? previousSentEndMsgId = null)
            {
                LastReceivedMsgId = lastReceivedMsgId;
                LastSentMsgId = lastSentMsgId;
                LastWindowStart = lastWindowStart;
                LastPackageSize = lastPackageSize;
                LastSentMsgType = lastSentMsgType;
                PreviousSentEndMsgId = previousSentEndMsgId;
            }

            public void ReleaseEnumerator()
            {
                LastWindowStart = default;
            }
        }

        private ShieldedDictNc<string, GossipState> _gossipStates = new ShieldedDictNc<string, GossipState>(StringComparer.InvariantCultureIgnoreCase);

        private bool HasTimedOut(GossipState state) =>
            unchecked(Environment.TickCount - state.CreationTickCount) >= Configuration.AntiEntropyIdleTimeout;

        private bool IsGossipActive(string server) => Shield.InTransaction(() =>
        {
            if (!_gossipStates.TryGetValue(server, out var state) || state.LastSentMsgType == MessageType.End)
                return false;
            if (HasTimedOut(state))
            {
                state.ReleaseEnumerator();
                return false;
            }
            return true;
        });

        private void SpreadRumors()
        {
            try
            {
                Shield.InTransaction(() =>
                {
                    var servers = Transport.Servers;
                    if (servers == null || !servers.Any())
                        return;
                    var limit = Configuration.AntiEntropyHuntingLimit;
                    var rand = new Random();
                    string server;
                    do
                    {
                        server = servers.Skip(rand.Next(Transport.ServerCount)).First();
                    }
                    while (!StartGossip(server) && --limit >= 0);
                });
            }
            catch (Exception ex)
            {
                RaiseError(new GossipBackendException("Unexpected error on gossip timer task.", ex));
            }
        }

        private bool StartGossip(string server) => Shield.InTransaction(() =>
        {
            if (IsGossipActive(server))
                return false;
            var lastReceivedId = _gossipStates.TryGetValue(server, out var oldState) ? oldState.LastReceivedMsgId : null;
            var toSend = GetPackage(Configuration.AntiEntropyInitialSize, default, null, null, null, out var newWindowStart);
            var msg = new GossipStart
            {
                From = Transport.OwnId,
                DatabaseHash = _freshIndex.DatabaseHash,
                Items = toSend.Length == 0 ? null : toSend,
                WindowStart = toSend.Length == 0 || newWindowStart.IsDone ? 0 : newWindowStart.Current.Freshness,
                WindowEnd = toSend.Length == 0 ? _freshIndex.LastFreshness : toSend[0].Freshness,
                ReplyToId = lastReceivedId,
            };
            _gossipStates[server] = new GossipState(null, msg.MessageId, newWindowStart, Configuration.AntiEntropyInitialSize,
                MessageType.Start, oldState?.LastSentMsgType == MessageType.End ? (int?)oldState.LastSentMsgId : null);
            Shield.SideEffect(() => Transport.Send(server, msg, true));
            return true;
        });

        private object Transport_MessageHandler(object msg)
        {
            switch (msg)
            {
                case DirectMail trans:
                    ApplyItems(trans.Items, true);
                    return null;

                case GossipMessage gossip:
                    var pkg = gossip as NewGossip;
                    long? ignoreUpToFreshness = null;
                    HashSet<string> keysToIgnore = null;
                    if (pkg?.Items != null && gossip.DatabaseHash != _freshIndex.DatabaseHash)
                    {
                        Shield.InTransaction(() =>
                        {
                            keysToIgnore = ApplyItems(pkg.Items, true);
                            if (keysToIgnore != null)
                            {
                                if (!_local.Changes.Any())
                                    ignoreUpToFreshness = _freshIndex.LastFreshness;
                                else
                                    Shield.SyncSideEffect(() =>
                                    {
                                        // we don't want to send back the same things we just received. so, ignore all keys from
                                        // the incoming msg for which the result of application was Greater or Equal, which means our
                                        // local value is (now) identical to the received one, unless they also appear in _toMail,
                                        // which means a Changed handler made further changes to them.
                                        // we need the max freshness in case these fields change after this transaction.
                                        ignoreUpToFreshness = _freshIndex.LastFreshness;
                                        if (_toMail.HasValue)
                                            keysToIgnore.ExceptWith(_toMail.Value.Keys);
                                    });
                            }
                        });
                    }
                    return GetReply(gossip, ignoreUpToFreshness, keysToIgnore);

                case KillGossip kill:
                    Shield.InTransaction(() =>
                    {
                        if (_gossipStates.TryGetValue(kill.From, out var state) && state.LastSentMsgId == kill.ReplyToId)
                            _gossipStates.Remove(kill.From);
                    });
                    return null;

                default:
                    throw new ApplicationException($"Unexpected message type: {msg.GetType()}");
            }
        }

        private readonly ApplyMethods _applyMethods;

        private static readonly ShieldedLocal<long> _freshnessContext = new ShieldedLocal<long>();

        /// <summary>
        /// Applies the given items internally, does not cause any direct mail. Applies them
        /// starting from the last, and if they have different Freshness values, they will be
        /// indexed in this backend with different values too. It is assumed they are sorted
        /// by descending freshness. Result contains keys whose values are (now) equal in our
        /// DB to the received values.
        /// </summary>
        internal HashSet<string> ApplyItems(MessageItem[] items, bool respectFreshness) => Shield.InTransaction(() =>
        {
            if (items == null || items.Length == 0)
                return null;
            long prevItemFreshness = items[items.Length - 1].Freshness;
            bool freshnessUtilized = false;
            HashSet<string> equalKeys = null;
            for (var i = items.Length - 1; i >= 0; i--)
            {
                var item = items[i];
                if (item.Data == null)
                    continue;
                if (_local.TryGetValue(item.Key, out var curr) &&
                    item.Deleted == curr.Deleted && item.Expired == curr.Expired &&
                    Util.RoughlyEqual(item.ExpiresInMs, curr.ExpiresInMs, Configuration.ExpiryComparePrecision) &&
                    Util.IsByteEqual(curr.Data, item.Data))
                {
                    if (equalKeys == null)
                        equalKeys = new HashSet<string>();
                    equalKeys.Add(item.Key);
                    continue;
                }
                if (respectFreshness && prevItemFreshness != item.Freshness)
                {
                    prevItemFreshness = item.Freshness;
                    if (freshnessUtilized)
                        _freshnessContext.Value = _freshnessContext.GetValueOrDefault() + 1;
                    freshnessUtilized = false;
                }
                var obj = item.Value;
                var method = _applyMethods.Get(obj.GetType());
                var itemResult = method(item.Key, obj, item.Deleted, item.Expired, item.ExpiresInMs);
                freshnessUtilized |= itemResult != ComplexRelationship.Less && itemResult != ComplexRelationship.Equal &&
                    itemResult != ComplexRelationship.EqualButLess;
                if (itemResult == ComplexRelationship.Greater || itemResult == ComplexRelationship.Equal ||
                    itemResult == ComplexRelationship.EqualButGreater)
                {
                    if (equalKeys == null)
                        equalKeys = new HashSet<string>();
                    equalKeys.Add(item.Key);
                }
            }
            return equalKeys;
        });

        private bool ShouldReply(GossipMessage msg, out GossipState currentState, out bool sendKill)
        {
            currentState = null;
            sendKill = false;
            var isStarter = msg is GossipStart;
            var hisReply = msg as GossipReply;
            // if our state is obsolete, we will only accept starter messages.
            if (!_gossipStates.TryGetValue(msg.From, out var state) || HasTimedOut(state))
            {
                sendKill = hisReply != null;
                return isStarter;
            }

            // we have an active state. handling starter messages first.
            if (isStarter)
            {
                // this means he was aware of our last message, whatever it was, and chose to send us this. OK.
                // this may happen if he sent us a GossipEnd before, but we did not receive it (yet).
                if (msg.ReplyToId == state.LastSentMsgId)
                    return true;
                else
                    // otherwise, we can only accept a starter if our msg was an end msg, or in case of simultaneous start,
                    // if the other server has higher "prio".
                    return state.LastSentMsgType == MessageType.End ||
                        state.LastSentMsgType == MessageType.Start &&
                            StringComparer.InvariantCultureIgnoreCase.Compare(msg.From, Transport.OwnId) < 0;
            }

            // he's replying. special case: he's replying to our end message, and we already sent a GossipStart after
            // that end message. we give preference to continuing the old chain in that case.
            if (state.LastSentMsgType == MessageType.Start &&
                state.PreviousSentEndMsgId != null && state.PreviousSentEndMsgId == msg.ReplyToId)
            {
                // when replying to our end message, it must be a GossipReply and he should send us LastWindowStart == 0.
                if (hisReply == null || hisReply.LastWindowStart > 0)
                    throw new ApplicationException("Reply chain logic failure.");
                return true;
            }
            // otherwise if he's replying to something else, he must send us a correct ReplyToId
            if (state.LastSentMsgId != msg.ReplyToId)
            {
                sendKill = hisReply != null && hisReply.MessageId != state.LastReceivedMsgId;
                return false;
            }

            // so, he's replying. this is just a safety check, to see if the windows match. they will.
            var ourLastStart = hisReply?.LastWindowStart ?? 0;
            if (ourLastStart > 0 && ourLastStart != (state.LastWindowStart.IsDone ? 0 : state.LastWindowStart.Current.Freshness))
                throw new ApplicationException("Reply chain logic failure.");

            // OK, everything checks out
            currentState = state;
            return true;
        }

        private object GetReply(GossipMessage replyTo,
            long? ignoreUpToFreshness = null, HashSet<string> keysToIgnore = null) => Shield.InTransaction<object>(() =>
        {
            var server = replyTo.From;
            var hisNews = replyTo as NewGossip;
            var hisReply = replyTo as GossipReply;
            var hisEnd = replyTo as GossipEnd;

            if (!ShouldReply(replyTo, out var currentState, out var sendKill))
            {
                if (sendKill)
                    return new KillGossip { From = Transport.OwnId, ReplyToId = replyTo.MessageId };
                return null;
            }

            var lastWindowStart = hisReply?.LastWindowStart ?? 0;
            var lastWindowEnd = hisReply?.LastWindowEnd ?? hisEnd?.LastWindowEnd;

            var ownHash = _freshIndex.DatabaseHash;
            if (ownHash == replyTo.DatabaseHash)
            {
                if (hisEnd != null)
                {
                    _gossipStates.Remove(server);
                    return null;
                }
                else
                    return PrepareEnd(hisNews, currentState?.LastPackageSize ?? 0, true);
            }

            var packageSize = currentState == null
                ? Configuration.AntiEntropyInitialSize
                : Math.Max(Configuration.AntiEntropyInitialSize,
                    Math.Min(Configuration.AntiEntropyItemsCutoff, currentState.LastPackageSize * 2));
            var toSend = GetPackage(packageSize,
                lastWindowStart > 0 ? currentState.LastWindowStart : default, lastWindowEnd,
                ignoreUpToFreshness, keysToIgnore, out var newStartEnumerator);

            if (toSend.Length == 0)
            {
                if (hisNews == null)
                {
                    _gossipStates.Remove(server);
                    return null;
                }
                else if (hisNews.Items == null || hisNews.Items.Length == 0)
                    return PrepareEnd(hisNews, currentState?.LastPackageSize ?? 0, false);
                else
                    return PrepareReply(server, new GossipReply
                    {
                        From = Transport.OwnId,
                        DatabaseHash = ownHash,
                        Items = null,
                        WindowStart = 0,
                        WindowEnd = _freshIndex.LastFreshness,
                        LastWindowStart = hisNews.WindowStart,
                        LastWindowEnd = hisNews.WindowEnd,
                        ReplyToId = replyTo.MessageId,
                    }, newStartEnumerator, currentState?.LastPackageSize ?? 0, currentState != null);
            }

            var windowStart = newStartEnumerator.IsDone ? 0 : newStartEnumerator.Current.Freshness;
            var windowEnd = _freshIndex.LastFreshness;
            return PrepareReply(server, new GossipReply
            {
                From = Transport.OwnId,
                DatabaseHash = ownHash,
                Items = toSend,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                LastWindowStart = hisNews?.WindowStart ?? 0,
                LastWindowEnd = (hisNews?.WindowEnd ?? hisEnd?.WindowEnd).Value,
                ReplyToId = replyTo.MessageId,
            }, newStartEnumerator, packageSize, currentState != null);
        });

        private GossipEnd PrepareEnd(NewGossip hisNews, int lastPackageSize, bool success)
        {
            var ourHash = _freshIndex.DatabaseHash;
            var maxFresh = _freshIndex.LastFreshness;
            var endMsg = new GossipEnd
            {
                From = Transport.OwnId,
                Success = success,
                DatabaseHash = ourHash,
                WindowEnd = maxFresh,
                LastWindowEnd = hisNews.WindowEnd,
                ReplyToId = hisNews.MessageId,
            };
            // if we're sending GossipEnd, we clear this in transaction, to make sure
            // IsGossipActive is correct, and to guarantee that we actually are done.
            _gossipStates[hisNews.From] = new GossipState(hisNews.MessageId, endMsg.MessageId, default, lastPackageSize, MessageType.End);
            return endMsg;
        }

        private GossipReply PrepareReply(string server, GossipReply msg, ReverseTimeIndex.Enumerator startEnumerator, int newPackageSize,
            bool hasActiveState)
        {
            // we try to keep the reply transaction read-only. but if we do not already have an active gossip state, then
            // we must set it consistently, to conflict with any possible concurrent reply or StartGossip process.
            if (!hasActiveState)
            {
                _gossipStates[server] = new GossipState(
                    msg.ReplyToId, msg.MessageId, startEnumerator, newPackageSize, MessageType.Reply);
            }
            else
            {
                Shield.SideEffect(() => Shield.InTransaction(() =>
                {
                    _gossipStates[server] = new GossipState(
                        msg.ReplyToId, msg.MessageId, startEnumerator, newPackageSize, MessageType.Reply);
                }));
            }
            return msg;
        }

        private MessageItem[] GetPackage(int packageSize, ReverseTimeIndex.Enumerator lastWindowStart, long? lastWindowEnd,
            long? ignoreUpToFreshness, HashSet<string> keysToIgnore, out ReverseTimeIndex.Enumerator newWindowStart)
        {
            if (packageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(packageSize), "The size of an anti-entropy package must be greater than zero.");

            var result = new List<MessageItem>();
            newWindowStart = _freshIndex.GetCloneableEnumerator();
            // first, if we have a last window, fetch all the new items that happened after lastWindowEnd.
            if (lastWindowEnd != null)
            {
                if (!AddPackagePart(result, ref newWindowStart, lastWindowEnd, null, ignoreUpToFreshness, keysToIgnore))
                    return result.ToArray();

                // and then prepare to continue where we left off last time
                newWindowStart = lastWindowStart;
                if (newWindowStart.IsDone)
                    return result.ToArray();
                // last time we iterated this one, we stopped without adding the current item. let's see if that item is still up to date.
                if (newWindowStart.Current.Item != GetItemDirect(newWindowStart.Current.Item.Key) && !newWindowStart.MoveNext())
                    return result.ToArray();
            }

            // and now, the (next) package of old items
            AddPackagePart(result, ref newWindowStart, null, packageSize, ignoreUpToFreshness, keysToIgnore);
            return result.ToArray();
        }

        // returns true if it added everything - either it reached stopAtFreshness, or it exhausted the enumerator.
        private bool AddPackagePart(List<MessageItem> result, ref ReverseTimeIndex.Enumerator enumerator,
            long? stopAtFreshness, int? packageSize, long? ignoreUpToFreshness, HashSet<string> keysToIgnore)
        {
            ReverseTimeIndex.Enumerator prevFreshnessStart = default;
            var itemsCutoff = Configuration.AntiEntropyItemsCutoff;
            var bytesCutoff = Configuration.AntiEntropyBytesCutoff;
            var bytes = result.Sum(mi => mi.Data.Length);

            if (!enumerator.IsOpen && !enumerator.MoveNext())
                return true;
            do
            {
                var item = enumerator.Current;
                if (item.Freshness <= stopAtFreshness)
                    return true;
                if (prevFreshnessStart.IsDone || prevFreshnessStart.Current.Freshness != item.Freshness)
                {
                    // bytes not checked, cause we do not know if this particular item will get added.
                    if (result.Count >= itemsCutoff || result.Count >= packageSize)
                        return false;
                    prevFreshnessStart = enumerator;
                }
                if (keysToIgnore == null || item.Freshness > ignoreUpToFreshness.Value || !keysToIgnore.Contains(item.Item.Key))
                {
                    bytes += item.Item.Data.Length;
                    if ((result.Count == itemsCutoff || bytes > bytesCutoff) &&
                        // we only stop and remove the current package if we already have at least one other package in result.
                        result.Count > 0 && result[0].Freshness != item.Freshness)
                    {
                        enumerator = prevFreshnessStart;
                        var index = result.FindIndex(mi => mi.Freshness == item.Freshness);
                        if (index >= 0)
                            result.RemoveRange(index, result.Count - index);
                        return false;
                    }
                    result.Add(item.Item);
                }
            }
            while (enumerator.MoveNext());
            return true;
        }

        /// <summary>
        /// Returns true if the backend contains a (non-deleted and non-expired) value under the key.
        /// </summary>
        public bool ContainsKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            return GetActiveItem(key) != null;
        }

        /// <summary>
        /// Try to read the value under the given key.
        /// </summary>
        public bool TryGet<TItem>(string key, out TItem item) where TItem : IMergeable<TItem>
        {
            var res = TryGet(key, out var obj);
            item = res ? (TItem)obj : default;
            return res;
        }

        /// <summary>
        /// Try to read the value under the given key.
        /// </summary>
        public bool TryGet(string key, out object item)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            item = default;
            var mi = GetActiveItem(key);
            if (mi == null)
                return false;
            item = mi.Value;
            return true;
        }

        /// <summary>
        /// Returns true if the backend contains a value under the key, including any expired or deleted value
        /// that still lingers.
        /// </summary>
        public bool ContainsKeyWithInfo(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            return GetItem(key) != null;
        }

        /// <summary>
        /// Try to read the value under the given key. Will return deleted and expired values as well,
        /// in case they are still present in the storage for communicating the removal to other servers.
        /// </summary>
        public FieldInfo<TItem> TryGetWithInfo<TItem>(string key) where TItem : IMergeable<TItem>
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            var mi = GetItem(key);
            return mi == null ? null : new FieldInfo<TItem>(mi);
        }

        /// <summary>
        /// Try to read the value under the given key. Will return deleted and expired values as well,
        /// in case they are still present in the storage for communicating the removal to other servers.
        /// </summary>
        public FieldInfo TryGetWithInfo(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            var mi = GetItem(key);
            return mi == null ? null : new FieldInfo(mi);
        }

        /// <summary>
        /// For internal use, does not raise the KeyMissing event. Needed for checking if an item in the
        /// fresh index is still current, because we do not want the data reloaded in that case.
        /// </summary>
        private MessageItem GetItemDirect(string key)
        {
            return _local.TryGetValue(key, out var mi) ? mi : null;
        }

        internal MessageItem GetItem(string key) => Shield.InTransaction(() =>
        {
            // expired items might be refetched by the KeyMissing event, so we do not consider those definitive.
            if (_local.TryGetValue(key, out MessageItem mi) && !(mi.Expired || mi.ExpiresInMs <= 0))
                return mi;

            var (val, deleted, expInMs) = OnKeyMissing(key);
            if (val == null)
                return mi;
            if (!deleted)
                deleted = val is IDeletable del && del.CanDelete;
            mi = new MessageItem
            {
                Key = key,
                Value = val,
                Deleted = deleted,
                ExpiresInMs = deleted ? null : expInMs,
                FreshnessOffset = _freshnessContext.GetValueOrDefault(),
            };
            _local[key] = mi;
            _freshIndex.Append(mi, GetHash(key, val));
            return mi;
        });

        internal MessageItem GetActiveItem(string key)
        {
            var i = GetItem(key);
            return i != null && IsActive(i) ? i : null;
        }

        private static bool IsActive(MessageItem mi) => !mi.Deleted && !mi.Expired && !(mi.ExpiresInMs <= 0);

        /// <summary>
        /// Gets all (non-deleted and non-expired) keys contained in the backend.
        /// </summary>
        public ICollection<string> Keys => Shield.InTransaction(() => _local.Where(kvp => IsActive(kvp.Value)).Select(kvp => kvp.Key).ToList());

        /// <summary>
        /// Gets all keys contained in the backend, including deleted and expired keys that still linger.
        /// </summary>
        public ICollection<string> KeysWithInfo => _local.Keys;

        private VersionHash GetHash(string key, IHasVersionBytes i)
        {
            return VersionHash.Hash(
                new[] { Encoding.UTF8.GetBytes(key) }
                .Concat(i.GetVersionBytes()));
        }

        /// <summary>
        /// A non-update, which ensures that when your local transaction is transmitted to other servers, this
        /// field will be transmitted as well, even if you did not change its value.
        /// </summary>
        /// <param name="key">The key to touch.</param>
        public void Touch(string key) => Shield.InTransaction(() =>
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            var mi = GetItem(key);
            if (mi == null)
                return;
            var newItem = new MessageItem
            {
                Key = key,
                Data = mi.Data,
                Deleted = mi.Deleted,
                Expired = mi.Expired,
                ExpiresInMs = mi.ExpiresInMs,
                FreshnessOffset = _freshnessContext.GetValueOrDefault(),
            };
            _local[key] = newItem;
            _freshIndex.Append(newItem, default);
            AddToMail(newItem);
        });

        private void AddToMail(MessageItem item)
        {
            var dict = _toMail.GetValueOrDefault();
            if (dict == null)
            {
                _toMail.Value = dict = new Dictionary<string, MessageItem>();
                Shield.SideEffect(() => DoDirectMail(dict.Values.ToArray()));
            }
            dict[item.Key] = item;
        }

        /// <summary>
        /// Set a value under the given key, merging it with any already existing value
        /// there. Returns the relationship of the new to the old value, or
        /// <see cref="VectorRelationship.Greater"/> if there is no old value.
        /// </summary>
        /// <param name="expireInMs">If given, the item will expire and be removed from the storage in
        /// this many milliseconds. If not null, must be > 0.</param>
        public VectorRelationship Set<TItem>(string key, TItem value, int? expireInMs = null) where TItem : IMergeable<TItem>
            => Shield.InTransaction(() =>
        {
            if (expireInMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(expireInMs));
            return SetInternalWithAddToMail(key, new FieldInfo<TItem>(value, expireInMs), addToMail: true).GetValueRelationship();
        });

        private ComplexRelationship SetInternal<TItem>(string key, FieldInfo<TItem> value)
            where TItem : IMergeable<TItem>
        {
            return SetInternalWithAddToMail(key, value, addToMail: false);
        }

        private ComplexRelationship SetInternalWithAddToMail<TItem>(string key, FieldInfo<TItem> value, bool addToMail)
            where TItem : IMergeable<TItem>
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            var oldItem = GetItem(key);
            if (oldItem != null)
            {
                var oldValue = new FieldInfo<TItem>(oldItem);
                var oldHash = oldItem.Deleted || oldItem.Expired ? default : GetHash(key, oldValue.Value);
                var (mergedValue, cmp) = value.MergeWith(oldValue, Configuration.ExpiryComparePrecision);
                if (cmp == ComplexRelationship.Less || cmp == ComplexRelationship.Equal || cmp == ComplexRelationship.EqualButLess)
                    return cmp;
                var newItem = new MessageItem
                {
                    Key = key,
                    Value = mergedValue.Value,
                    Deleted = mergedValue.Deleted,
                    Expired = mergedValue.Expired,
                    FreshnessOffset = _freshnessContext.GetValueOrDefault(),
                    ExpiresInMs = mergedValue.ExpiresInMs,
                };
                _local[key] = newItem;
                var hash = oldHash ^ (mergedValue.Deleted || mergedValue.Expired ? default : GetHash(key, mergedValue.Value));
                _freshIndex.Append(newItem, hash);
                if (addToMail)
                    AddToMail(newItem);

                if (cmp.GetValueRelationship() != VectorRelationship.Equal)
                    OnChanged(key, oldValue.Value, mergedValue.Value, mergedValue.Deleted);
                return cmp;
            }
            else
            {
                if (value.Deleted || value.Expired)
                    return ComplexRelationship.Equal;
                var newItem = new MessageItem
                {
                    Key = key,
                    Value = value.Value,
                    FreshnessOffset = _freshnessContext.GetValueOrDefault(),
                    ExpiresInMs = value.ExpiresInMs,
                };
                _local[key] = newItem;
                var hash = GetHash(key, value.Value);
                _freshIndex.Append(newItem, hash);
                if (addToMail)
                    AddToMail(newItem);

                if (value.ExpiresInMs == null || value.ExpiresInMs > 0)
                    OnChanged(key, null, value.Value, false);
                return ComplexRelationship.Greater;
            }
        }

        /// <summary>
        /// Remove the given key from the storage.
        /// </summary>
        public bool Remove(string key) => Shield.InTransaction(() => Remove(key, true));

        private bool Remove(string key, bool addToMail)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException(nameof(key));
            var oldItem = GetItem(key);
            if (oldItem == null || oldItem.Deleted)
                return false;

            var oldVal = oldItem.Expired ? default : (IHasVersionBytes)oldItem.Value;
            var hash = oldItem.Expired ? default : GetHash(key, oldVal);
            var newItem = new MessageItem
            {
                Key = key,
                Data = oldItem.Data,
                Deleted = true,
                FreshnessOffset = _freshnessContext.GetValueOrDefault(),
            };
            _local[key] = newItem;
            _freshIndex.Append(newItem, hash);
            if (addToMail)
                AddToMail(newItem);

            if (oldItem.Expired || oldItem.ExpiresInMs <= 0)
                return false;
            OnChanged(key, oldVal, oldVal, true);
            return true;
        }

        private void OnChanged(string key, object oldVal, object newVal, bool deleted)
        {
            var ev = new ChangedEventArgs(key, oldVal, newVal, deleted);
            Changed.Raise(this, ev);
        }

        /// <summary>
        /// Fired after any key changes.
        /// </summary>
        public ShieldedEvent<ChangedEventArgs> Changed { get; } = new ShieldedEvent<ChangedEventArgs>();

        private (IHasVersionBytes ValueToUse, bool Deleted, int? ExpiresInMs) OnKeyMissing(string key)
        {
            var ev = new KeyMissingEventArgs(key);
            KeyMissing.Raise(this, ev);
            return (ev.ValueToUse, ev.ValueDeleted, ev.ExpiresInMs);
        }

        /// <summary>
        /// Fired when accessing a key that has no value or has expired. Handlers can specify a value to use,
        /// which will be saved in the backend and returned to the original reader.
        /// </summary>
        public ShieldedEvent<KeyMissingEventArgs> KeyMissing { get; } = new ShieldedEvent<KeyMissingEventArgs>();

        public void Dispose()
        {
            Transport.Dispose();
            _gossipTimer.Dispose();
            _deletableTimer.Dispose();
        }

        /// <summary>
        /// An enumerable of keys read or written into by the current transaction. Includes
        /// keys that did not have a value.
        /// </summary>
        public IEnumerable<string> Reads => _local.Reads;

        /// <summary>
        /// An enumerable of keys written into by the current transaction.
        /// </summary>
        public IEnumerable<string> Changes => _local.Changes;
    }
}
