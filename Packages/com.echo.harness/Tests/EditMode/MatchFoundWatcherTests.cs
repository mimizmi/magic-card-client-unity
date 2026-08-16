using System;
using System.Collections.Generic;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class MatchFoundWatcherTests
    {
        private FakeProtocolSession session;
        private MatchFoundWatcher watcher;

        [SetUp]
        public void SetUp()
        {
            session = new FakeProtocolSession();
            watcher = new MatchFoundWatcher(session);
        }

        [TearDown]
        public void TearDown() => watcher?.Dispose();

        private void ServerFindsAMatch(
            string gameId = "room-4",
            int seat = 1,
            string opponent = "ada") =>
            session.PublishToSubscribers(MessageId.MatchFoundEvent, new MatchFoundEventDto
            {
                GameId = gameId,
                YourSeat = seat,
                OpponentName = opponent,
            });

        [Test]
        public void ConstructingWithoutASessionThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new MatchFoundWatcher(null));
        }

        /// <summary>
        /// The property the type exists for. Constructing the watcher - not
        /// observing it, not joining a queue - is what puts a subscriber on 2004,
        /// which is what makes the reconnect path's back-to-back LoginResponse and
        /// MatchFoundEvent survivable.
        /// </summary>
        [Test]
        public void TheSubscriptionExistsFromConstructionRatherThanFromTheFirstObserver()
        {
            Assert.That(session.SubscriberCount(MessageId.MatchFoundEvent), Is.EqualTo(1));
        }

        [Test]
        public void AMatchReachesAnObserverAndIsConvertedFieldByField()
        {
            var seen = new List<MatchFound>();
            using (watcher.Observe(seen.Add))
            {
                ServerFindsAMatch("room-4", 1, "ada");
            }

            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(seen[0].GameId, Is.EqualTo("room-4"));
            Assert.That(seen[0].Seat, Is.EqualTo(1));
            Assert.That(seen[0].OpponentName, Is.EqualTo("ada"));
        }

        [Test]
        public void NoMatchMeansNoLatest()
        {
            Assert.That(watcher.Latest, Is.Null);
        }

        /// <summary>
        /// Why <c>Latest</c> is nullable rather than a plain struct: seat 0 is a
        /// real seat, so a default <c>MatchFound</c> and a genuine "you are seat 0"
        /// pairing are the same bytes.
        /// </summary>
        [Test]
        public void ASeatZeroMatchIsDistinguishableFromNoMatchAtAll()
        {
            ServerFindsAMatch("room-1", 0, "bob");

            Assert.That(watcher.Latest, Is.Not.Null);
            Assert.That(watcher.Latest.Value.Seat, Is.Zero);
        }

        /// <summary>
        /// The replay. A view-model constructed after the event arrived - the
        /// ordinary case on the reconnect path, where the match lands during login
        /// and the screen is built afterwards - must still learn about it.
        /// </summary>
        [Test]
        public void AnObserverArrivingAfterTheMatchIsToldImmediately()
        {
            ServerFindsAMatch("room-9", 0, "carol");

            var seen = new List<MatchFound>();
            using (watcher.Observe(seen.Add))
            {
                Assert.That(seen.Count, Is.EqualTo(1),
                    "Observe replays the latest match before returning.");
                Assert.That(seen[0].GameId, Is.EqualTo("room-9"));
            }
        }

        // A replay of one, not a log. A player is in at most one game, so an older
        // pairing is not something a late observer should be handed.
        [Test]
        public void ASecondMatchSupersedesTheFirstForALateObserver()
        {
            ServerFindsAMatch("room-1", 0, "bob");
            ServerFindsAMatch("room-2", 1, "carol");

            var seen = new List<MatchFound>();
            using (watcher.Observe(seen.Add))
            {
                Assert.That(seen.Count, Is.EqualTo(1));
                Assert.That(seen[0].GameId, Is.EqualTo("room-2"));
            }
        }

        [Test]
        public void UnsubscribingStopsDelivery()
        {
            var seen = new List<MatchFound>();
            watcher.Observe(seen.Add).Dispose();

            ServerFindsAMatch();

            Assert.That(seen, Is.Empty);
        }

        [Test]
        public void DisposingTheWatcherDropsItsSubscription()
        {
            watcher.Dispose();

            Assert.That(session.SubscriberCount(MessageId.MatchFoundEvent), Is.Zero);
        }

        [Test]
        public void DisposingTwiceIsHarmless()
        {
            watcher.Dispose();
            Assert.DoesNotThrow(() => watcher.Dispose());
        }

        /// <summary>
        /// The trade the swallow in <c>OnMatchFound</c> makes, stated as a test: a
        /// broken observer costs its own delivery and nothing else. What it also
        /// costs - the DispatchFailure fault the session would otherwise raise - is
        /// recorded in that method's comment rather than here, because this fake
        /// dispatches directly and has no such conversion to observe.
        /// </summary>
        [Test]
        public void OneThrowingObserverDoesNotDenyTheOthersTheMatch()
        {
            var seen = new List<MatchFound>();
            using (watcher.Observe(_ => throw new InvalidOperationException("broken screen")))
            using (watcher.Observe(seen.Add))
            {
                Assert.DoesNotThrow(() => ServerFindsAMatch());
            }

            Assert.That(seen.Count, Is.EqualTo(1));
        }

        [Test]
        public void ObservingWithoutAHandlerThrows()
        {
            Assert.Throws<ArgumentNullException>(() => watcher.Observe(null));
        }
    }
}
