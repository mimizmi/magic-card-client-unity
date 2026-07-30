using System;
using System.Collections.Generic;
using System.Linq;
using Echo.Harness.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    /// <summary>
    /// Drives every assertion from the generated protocol fixture rather than
    /// from hand-written expectations, so a Go-side contract change surfaces
    /// here instead of at runtime.
    /// </summary>
    public sealed class ProtocolDtoContractTests
    {
        private static readonly DefaultContractResolver Resolver = new DefaultContractResolver();

        private static ProtocolContractDocument Fixture => ProtocolContractFixture.Load();

        /// <summary>
        /// Reads the declared Newtonsoft contract instead of serializing an
        /// instance. A property carrying NullValueHandling.Ignore vanishes from
        /// a default instance's JSON, so serialization would under-report the
        /// contract and let a missing property pass.
        /// </summary>
        private static IReadOnlyList<JsonProperty> DeclaredProperties(Type type)
        {
            var contract = (JsonObjectContract)Resolver.ResolveContract(type);
            return contract.Properties.ToArray();
        }

        [Test]
        public void FixtureNames_MatchTheMessageIdEnum()
        {
            foreach (var message in Fixture.Messages)
            {
                Assert.That(
                    Enum.GetName(typeof(MessageId), (MessageId)message.Id),
                    Is.EqualTo(message.Name),
                    $"Fixture name for id {message.Id} does not match the MessageId enum. " +
                    "The generator's csharpNames table is hand-maintained; fix it there.");
            }
        }

        [Test]
        public void RegisteredDtos_DeclareExactlyTheFixtureFieldNames()
        {
            foreach (var message in Fixture.Messages)
            {
                if (!ProtocolMessageMap.PayloadTypes.TryGetValue((MessageId)message.Id, out var type))
                {
                    continue;
                }

                Assert.That(
                    DeclaredProperties(type).Select(property => property.PropertyName),
                    Is.EquivalentTo(message.Payload.Fields.Select(field => field.JsonName)),
                    $"{type.Name} does not match the fixture contract for {message.Name}.");
            }
        }

        [Test]
        public void RegisteredDtos_UseNullableTypesForNullableFields()
        {
            foreach (var message in Fixture.Messages)
            {
                if (!ProtocolMessageMap.PayloadTypes.TryGetValue((MessageId)message.Id, out var type))
                {
                    continue;
                }

                AssertNullability(type, message.Payload.Fields, message.Name);
            }
        }

        [Test]
        public void EmptyPayloadMessages_SerializeToAnEmptyObject()
        {
            var empties = Fixture.Messages.Where(m => m.Payload.Shape == "empty").ToArray();
            Assert.That(empties, Is.Not.Empty, "The fixture should contain empty-payload messages.");

            foreach (var message in empties)
            {
                Assert.That(
                    ProtocolMessageMap.PayloadTypes.ContainsKey((MessageId)message.Id),
                    Is.True,
                    $"{message.Name} has an empty payload and still needs a registered DTO.");

                var type = ProtocolMessageMap.PayloadTypes[(MessageId)message.Id];
                Assert.That(DeclaredProperties(type), Is.Empty, $"{type.Name} must declare no properties.");
                Assert.That(
                    JsonConvert.SerializeObject(Activator.CreateInstance(type)),
                    Is.EqualTo("{}"),
                    $"{type.Name} must serialize to an empty JSON object.");
            }
        }

        [Test]
        public void NoPayloadMessages_HaveNoRegisteredDto()
        {
            var noPayload = Fixture.Messages.Where(m => m.Payload.Shape == "none").ToArray();
            Assert.That(noPayload, Is.Not.Empty, "The fixture should contain payload-free messages.");

            foreach (var message in noPayload)
            {
                Assert.That(
                    ProtocolMessageMap.PayloadTypes.ContainsKey((MessageId)message.Id),
                    Is.False,
                    $"{message.Name} carries no payload and must not have a registered DTO.");
            }
        }

        [Test]
        public void EveryMessageWithAPayload_HasARegisteredDto()
        {
            var missing = Fixture.Messages
                .Where(message => message.Payload.Shape != "none")
                .Where(message => !ProtocolMessageMap.PayloadTypes.ContainsKey((MessageId)message.Id))
                .Select(message => $"{message.Id} {message.Name}")
                .ToArray();

            Assert.That(missing, Is.Empty, "These messages still need a typed DTO.");
        }

        [Test]
        public void EveryNestedType_HasARegisteredDtoMatchingTheFixture()
        {
            Assert.That(
                ProtocolMessageMap.NestedTypes.Keys,
                Is.EquivalentTo(Fixture.Types.Keys),
                "The nested type registry and the fixture types disagree.");

            foreach (var entry in Fixture.Types)
            {
                var type = ProtocolMessageMap.NestedTypes[entry.Key];

                Assert.That(
                    DeclaredProperties(type).Select(property => property.PropertyName),
                    Is.EquivalentTo(entry.Value.Fields.Select(field => field.JsonName)),
                    $"{type.Name} does not match the fixture contract for {entry.Key}.");

                AssertNullability(type, entry.Value.Fields, entry.Key);
            }
        }

        /// <summary>
        /// Without this, a structurally wrong mapping passes every other test:
        /// declaring GameStateEventDto.Me as a string satisfies both the name
        /// set and the nullability rule.
        /// </summary>
        [Test]
        public void FieldsWithATypeRef_UseTheMatchingNestedDto()
        {
            foreach (var message in Fixture.Messages)
            {
                if (ProtocolMessageMap.PayloadTypes.TryGetValue((MessageId)message.Id, out var type))
                {
                    AssertTypeRefs(type, message.Payload.Fields, message.Name);
                }
            }

            foreach (var entry in Fixture.Types)
            {
                AssertTypeRefs(ProtocolMessageMap.NestedTypes[entry.Key], entry.Value.Fields, entry.Key);
            }
        }

        private static void AssertTypeRefs(
            Type type,
            IReadOnlyList<ProtocolFieldDocument> fields,
            string context)
        {
            var declared = DeclaredProperties(type)
                .ToDictionary(property => property.PropertyName, property => property);

            foreach (var field in fields)
            {
                Assert.That(
                    declared.ContainsKey(field.JsonName),
                    Is.True,
                    $"{context}: {type.Name} is missing '{field.JsonName}'.");

                // Arity, asserted for EVERY field and not only the ones carrying
                // a type_ref. Nothing else in the suite reads
                // ProtocolFieldDocument.Repeated, and ElementType below unwraps
                // IReadOnlyList<T> unconditionally, so without this a repeated
                // field declared as a bare element type compared equal. The two
                // fields it closes outright are GameConfigEvent.characters and
                // .fields, which carry no type_ref and have no serialization
                // test: declaring either as JObject rather than
                // IReadOnlyList<JObject> left the whole suite green while the
                // server's first GameConfigEvent would decode to a
                // MalformedPayload fault.
                Assert.That(
                    IsRepeatedShape(declared[field.JsonName].PropertyType),
                    Is.EqualTo(field.Repeated),
                    $"{context}: '{field.JsonName}' is {field.GoType} in Go, so it must " +
                    (field.Repeated
                        ? "be an IReadOnlyList<T> and is not."
                        : "not be a collection and is one."));

                if (string.IsNullOrEmpty(field.TypeRef))
                {
                    continue;
                }

                Assert.That(
                    ProtocolMessageMap.NestedTypes.ContainsKey(field.TypeRef),
                    Is.True,
                    $"{context}: '{field.JsonName}' references unknown nested type '{field.TypeRef}'.");

                var expected = ProtocolMessageMap.NestedTypes[field.TypeRef];
                var actual = ElementType(declared[field.JsonName].PropertyType);

                Assert.That(
                    actual,
                    Is.EqualTo(expected),
                    $"{context}: '{field.JsonName}' is {field.GoType} in Go and must map to " +
                    $"{expected.Name}, not {actual.Name}.");
            }
        }

        /// <summary>
        /// The collection shape a repeated Go field maps to. Deliberately the
        /// same shape <see cref="ElementType"/> unwraps, so a repeated field's
        /// arity assertion and its type_ref assertion cannot disagree: if this
        /// returns true the element type below is the one actually compared.
        /// </summary>
        private static bool IsRepeatedShape(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);
        }

        /// <summary>
        /// Unwraps IReadOnlyList&lt;T&gt; so a repeated field compares against
        /// its element type. Non-collection types are returned unchanged.
        /// </summary>
        private static Type ElementType(Type type)
        {
            if (IsRepeatedShape(type))
            {
                return type.GetGenericArguments()[0];
            }

            return type;
        }

        private static void AssertNullability(
            Type type,
            IReadOnlyList<ProtocolFieldDocument> fields,
            string context)
        {
            var declared = DeclaredProperties(type)
                .ToDictionary(property => property.PropertyName, property => property);

            foreach (var field in fields)
            {
                Assert.That(
                    declared.ContainsKey(field.JsonName),
                    Is.True,
                    $"{context}: {type.Name} is missing '{field.JsonName}'.");

                if (!field.Nullable)
                {
                    continue;
                }

                var propertyType = declared[field.JsonName].PropertyType;
                var isNullable =
                    !propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) != null;

                Assert.That(
                    isNullable,
                    Is.True,
                    $"{context}: '{field.JsonName}' is {field.GoType} in Go, which can marshal to " +
                    $"null, so it must be a nullable C# type rather than {propertyType.Name}.");
            }
        }
    }
}
