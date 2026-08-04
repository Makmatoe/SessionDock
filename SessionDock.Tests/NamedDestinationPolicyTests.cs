using SessionDock.Models;
using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class NamedDestinationPolicyTests
{
    [Fact]
    public void NormalizeInPlace_HardensMalformedDeserializedCollections()
    {
        const string accountKey = "account-alpha";
        var firstAccount = new AccountProfile
        {
            Key = accountKey,
            Destination = "99999"
        };
        var duplicateAccount = new AccountProfile
        {
            Key = accountKey,
            Destination = "88888"
        };
        var settings = new AppSettings
        {
            Accounts =
            [
                firstAccount,
                duplicateAccount,
                null!,
                new AccountProfile { Key = null! }
            ],
            NamedDestinations =
            [
                null!,
                new NamedDestination
                {
                    Id = "legacy-stable-id",
                    Name = "Farm",
                    Value = "12345",
                    AccountKeys = null!
                },
                new NamedDestination
                {
                    Id = "legacy-stable-id",
                    Name = "farm",
                    Value = "67890",
                    AccountKeys = [accountKey, accountKey, null!, "missing"]
                },
                new NamedDestination
                {
                    Id = null!,
                    Name = null!,
                    Value = "not a Roblox destination",
                    AccountKeys = [accountKey]
                }
            ]
        };

        var changed = NamedDestinationPolicy.NormalizeInPlace(settings);

        Assert.True(changed);
        Assert.Equal(2, settings.NamedDestinations.Count);
        Assert.Equal(
            settings.NamedDestinations.Count,
            settings.NamedDestinations
                .Select(destination => destination.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            settings.NamedDestinations.Count,
            settings.NamedDestinations
                .Select(destination => destination.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(
            settings.NamedDestinations,
            destination => Assert.NotNull(destination.AccountKeys));
        Assert.Empty(settings.NamedDestinations[0].AccountKeys);
        Assert.Equal(
            new[] { accountKey },
            settings.NamedDestinations[1].AccountKeys);
        Assert.Equal("67890", firstAccount.Destination);
        Assert.Equal("67890", duplicateAccount.Destination);
    }

    [Fact]
    public void Upsert_EnforcesOneAssignmentAndMirrorsTheNamedValue()
    {
        var alpha = new AccountProfile
        {
            Key = "alpha",
            Destination = "11111"
        };
        var beta = new AccountProfile
        {
            Key = "beta",
            Destination = "22222"
        };
        var settings = new AppSettings
        {
            Accounts = [alpha, beta],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "old-id",
                    Name = "Old",
                    Value = "11111",
                    AccountKeys = [alpha.Key]
                }
            ]
        };

        var saved = NamedDestinationPolicy.TryUpsert(
            settings,
            destinationId: null,
            name: "Shared",
            value: "33333",
            accountKeys: [alpha.Key, beta.Key, alpha.Key, "missing"],
            out var savedId,
            out var errorKey);

        Assert.True(saved);
        Assert.Empty(errorKey);
        Assert.False(string.IsNullOrWhiteSpace(savedId));
        Assert.Empty(settings.NamedDestinations.Single(destination =>
            destination.Name == "Old").AccountKeys);
        var shared = settings.NamedDestinations.Single(destination =>
            destination.Name == "Shared");
        Assert.Equal(new[] { alpha.Key, beta.Key }, shared.AccountKeys);
        Assert.Equal("33333", alpha.Destination);
        Assert.Equal("33333", beta.Destination);
    }

    [Fact]
    public void UpsertEditing_ClearsOnlyRemovedAccountsStillUsingPriorValue()
    {
        var retained = new AccountProfile
        {
            Key = "retained",
            Destination = "12345"
        };
        var removed = new AccountProfile
        {
            Key = "removed",
            Destination = "12345"
        };
        var custom = new AccountProfile
        {
            Key = "custom",
            Destination = "99999"
        };
        var other = new AccountProfile
        {
            Key = "other",
            Destination = "22222"
        };
        var settings = new AppSettings
        {
            Accounts = [retained, removed, custom, other],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "edited-id",
                    Name = "Farm",
                    Value = "12345",
                    AccountKeys =
                        [retained.Key, removed.Key, custom.Key]
                },
                new NamedDestination
                {
                    Id = "other-id",
                    Name = "Trading",
                    Value = "22222",
                    AccountKeys = [other.Key]
                }
            ]
        };

        var saved = NamedDestinationPolicy.TryUpsert(
            settings,
            destinationId: "edited-id",
            name: "Farm updated",
            value: "54321",
            accountKeys: [retained.Key],
            out var savedId,
            out var errorKey);

        Assert.True(saved);
        Assert.Equal("edited-id", savedId);
        Assert.Empty(errorKey);
        Assert.Equal("54321", retained.Destination);
        Assert.Null(removed.Destination);
        Assert.Equal("99999", custom.Destination);
        Assert.Equal("22222", other.Destination);
        Assert.Equal(
            new[] { retained.Key },
            settings.NamedDestinations.Single(destination =>
                destination.Id == "edited-id").AccountKeys);
        Assert.Equal(
            new[] { other.Key },
            settings.NamedDestinations.Single(destination =>
                destination.Id == "other-id").AccountKeys);
    }

    [Fact]
    public void CustomValue_UnassignsTheAccountWithoutChangingOtherAccounts()
    {
        var alpha = new AccountProfile { Key = "alpha" };
        var beta = new AccountProfile { Key = "beta" };
        var settings = new AppSettings
        {
            Accounts = [alpha, beta],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "shared-id",
                    Name = "Shared",
                    Value = "12345",
                    AccountKeys = [alpha.Key, beta.Key]
                }
            ]
        };
        NamedDestinationPolicy.NormalizeInPlace(settings);

        NamedDestinationPolicy.SetCustomDestination(
            settings,
            alpha.Key,
            "67890");

        Assert.Equal("67890", alpha.Destination);
        Assert.Equal("12345", beta.Destination);
        Assert.Equal(
            new[] { beta.Key },
            Assert.Single(settings.NamedDestinations).AccountKeys);
    }

    [Fact]
    public void AccountDestination_UniqueSavedTargetUpdatesChecklistAssignment()
    {
        var account = new AccountProfile
        {
            Key = "alpha",
            Destination = "67890"
        };
        var settings = new AppSettings
        {
            Accounts = [account],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "farm-id",
                    Name = "Farm",
                    Value = "12345"
                }
            ]
        };

        var assignedId = NamedDestinationPolicy.SetAccountDestination(
            settings,
            account.Key,
            "12345");

        Assert.Equal("farm-id", assignedId);
        Assert.Equal("12345", account.Destination);
        Assert.Equal(
            new[] { account.Key },
            Assert.Single(settings.NamedDestinations).AccountKeys);
        Assert.Equal(
            "farm-id",
            NamedDestinationPolicy.GetAssignedDestinationId(
                settings,
                account.Key));
    }

    [Fact]
    public void AccountDestination_EquivalentOfficialUrlUsesSavedAssignment()
    {
        var account = new AccountProfile { Key = "alpha" };
        var settings = new AppSettings
        {
            Accounts = [account],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "farm-id",
                    Name = "Farm",
                    Value = "https://www.roblox.com/games/12345/Farm"
                }
            ]
        };

        var assignedId = NamedDestinationPolicy.SetAccountDestination(
            settings,
            account.Key,
            "12345");

        Assert.Equal("farm-id", assignedId);
        Assert.Equal(
            "https://www.roblox.com/games/12345/Farm",
            account.Destination);
        Assert.Equal(
            new[] { account.Key },
            Assert.Single(settings.NamedDestinations).AccountKeys);
    }

    [Fact]
    public void AccountDestination_AmbiguousSavedTargetPreservesExistingChoice()
    {
        var account = new AccountProfile
        {
            Key = "alpha",
            Destination = "12345"
        };
        var settings = new AppSettings
        {
            Accounts = [account],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "first-id",
                    Name = "First",
                    Value = "12345",
                    AccountKeys = [account.Key]
                },
                new NamedDestination
                {
                    Id = "second-id",
                    Name = "Second",
                    Value = "https://www.roblox.com/games/12345/Second"
                }
            ]
        };

        var assignedId = NamedDestinationPolicy.SetAccountDestination(
            settings,
            account.Key,
            "https://www.roblox.com/games/12345/Edited");

        Assert.Equal("first-id", assignedId);
        Assert.Equal("12345", account.Destination);
        Assert.Equal(
            new[] { account.Key },
            settings.NamedDestinations.Single(destination =>
                destination.Id == "first-id").AccountKeys);
        Assert.Empty(settings.NamedDestinations.Single(destination =>
            destination.Id == "second-id").AccountKeys);
    }

    [Fact]
    public void AccountDestination_AmbiguousNewTargetRemainsCustomWithoutDataLoss()
    {
        const string requested =
            "https://www.roblox.com/games/12345/User-selected-form";
        var account = new AccountProfile
        {
            Key = "alpha",
            Destination = "67890"
        };
        var other = new AccountProfile
        {
            Key = "beta",
            Destination = "12345"
        };
        var settings = new AppSettings
        {
            Accounts = [account, other],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "first-id",
                    Name = "First",
                    Value = "12345",
                    AccountKeys = [other.Key]
                },
                new NamedDestination
                {
                    Id = "second-id",
                    Name = "Second",
                    Value = "https://www.roblox.com/games/12345/Second"
                }
            ]
        };

        var assignedId = NamedDestinationPolicy.SetAccountDestination(
            settings,
            account.Key,
            requested);

        Assert.Null(assignedId);
        Assert.Equal(requested, account.Destination);
        Assert.Equal("12345", other.Destination);
        Assert.Equal(
            new[] { other.Key },
            settings.NamedDestinations.Single(destination =>
                destination.Id == "first-id").AccountKeys);
        Assert.Empty(settings.NamedDestinations.Single(destination =>
            destination.Id == "second-id").AccountKeys);
    }

    [Fact]
    public void AccountDestination_CustomValueRemovesOnlyThatAccountsAssignment()
    {
        var alpha = new AccountProfile
        {
            Key = "alpha",
            Destination = "12345"
        };
        var beta = new AccountProfile
        {
            Key = "beta",
            Destination = "12345"
        };
        var settings = new AppSettings
        {
            Accounts = [alpha, beta],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "shared-id",
                    Name = "Shared",
                    Value = "12345",
                    AccountKeys = [alpha.Key, beta.Key]
                }
            ]
        };

        var assignedId = NamedDestinationPolicy.SetAccountDestination(
            settings,
            alpha.Key,
            "67890");

        Assert.Null(assignedId);
        Assert.Equal("67890", alpha.Destination);
        Assert.Equal("12345", beta.Destination);
        Assert.Equal(
            new[] { beta.Key },
            Assert.Single(settings.NamedDestinations).AccountKeys);
    }

    [Fact]
    public void Delete_PreservesAssignedAccountsAsLegacyCustomValues()
    {
        var account = new AccountProfile { Key = "alpha" };
        var settings = new AppSettings
        {
            Accounts = [account],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "saved-id",
                    Name = "Farm",
                    Value = "12345",
                    AccountKeys = [account.Key]
                }
            ]
        };
        NamedDestinationPolicy.NormalizeInPlace(settings);

        var removed = NamedDestinationPolicy.Delete(settings, "saved-id");

        Assert.True(removed);
        Assert.Empty(settings.NamedDestinations);
        Assert.Equal("12345", account.Destination);
    }

    [Fact]
    public void Upsert_ReturnsActionableValidationKeys()
    {
        var settings = new AppSettings
        {
            Accounts = [new AccountProfile { Key = "alpha" }],
            NamedDestinations =
            [
                new NamedDestination
                {
                    Id = "saved-id",
                    Name = "Farm",
                    Value = "12345"
                }
            ]
        };

        Assert.False(NamedDestinationPolicy.TryUpsert(
            settings,
            null,
            " ",
            "12345",
            [],
            out _,
            out var nameError));
        Assert.Equal("Validation.NamedDestination.NameRequired", nameError);

        Assert.False(NamedDestinationPolicy.TryUpsert(
            settings,
            null,
            "Other",
            "not a valid destination",
            [],
            out _,
            out var valueError));
        Assert.Equal("Validation.NamedDestination.ValueInvalid", valueError);

        Assert.False(NamedDestinationPolicy.TryUpsert(
            settings,
            null,
            "farm",
            "67890",
            [],
            out _,
            out var duplicateError));
        Assert.Equal("Validation.NamedDestination.NameUnique", duplicateError);
    }

    [Fact]
    public void NormalizeInPlace_AcceptsNullDeserializedRootCollections()
    {
        var settings = new AppSettings
        {
            Accounts = null!,
            NamedDestinations = null!
        };

        var changed = NamedDestinationPolicy.NormalizeInPlace(settings);

        Assert.True(changed);
        Assert.Empty(settings.Accounts);
        Assert.Empty(settings.NamedDestinations);
    }
}
