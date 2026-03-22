using FluentAssertions;

namespace HomeManagement.Auth.Tests;

/// <summary>
/// Tests for <see cref="RbacService"/> — role-based permission checks.
/// </summary>
public sealed class RbacServiceTests
{
    // ── HasPermission ──

    [Theory]
    [InlineData("Viewer", Permissions.MachinesRead, true)]
    [InlineData("Viewer", Permissions.PatchesRead, true)]
    [InlineData("Viewer", Permissions.AuditRead, true)]
    [InlineData("Viewer", Permissions.MachinesWrite, false)]
    [InlineData("Viewer", Permissions.AdminUsers, false)]
    [InlineData("Operator", Permissions.MachinesWrite, true)]
    [InlineData("Operator", Permissions.PatchesApply, true)]
    [InlineData("Operator", Permissions.JobsCancel, true)]
    [InlineData("Operator", Permissions.AdminUsers, false)]
    [InlineData("Admin", Permissions.AdminUsers, true)]
    [InlineData("Admin", Permissions.AdminSettings, true)]
    [InlineData("Auditor", Permissions.AuditExport, true)]
    [InlineData("Auditor", Permissions.MachinesWrite, false)]
    [InlineData("Auditor", Permissions.AdminUsers, false)]
    public void HasPermission_SingleRole_ReturnsExpectedResult(
        string role, string permission, bool expected)
    {
        RbacService.HasPermission([role], permission).Should().Be(expected);
    }

    [Fact]
    public void HasPermission_UnknownRole_ReturnsFalse()
    {
        RbacService.HasPermission(["NonExistent"], Permissions.MachinesRead).Should().BeFalse();
    }

    [Fact]
    public void HasPermission_EmptyRoles_ReturnsFalse()
    {
        RbacService.HasPermission([], Permissions.MachinesRead).Should().BeFalse();
    }

    [Fact]
    public void HasPermission_MultipleRoles_UnionGrants()
    {
        // Viewer has AuditRead, Operator has JobsCancel — together they cover both.
        var roles = new[] { "Viewer", "Operator" };

        RbacService.HasPermission(roles, Permissions.AuditRead).Should().BeTrue();
        RbacService.HasPermission(roles, Permissions.JobsCancel).Should().BeTrue();
    }

    [Fact]
    public void HasPermission_IsCaseInsensitive()
    {
        RbacService.HasPermission(["viewer"], Permissions.MachinesRead).Should().BeTrue();
        RbacService.HasPermission(["ADMIN"], Permissions.AdminUsers).Should().BeTrue();
    }

    // ── GetEffectivePermissions ──

    [Fact]
    public void GetEffectivePermissions_Viewer_ReturnsFivePermissions()
    {
        var perms = RbacService.GetEffectivePermissions(["Viewer"]);
        perms.Should().HaveCount(5);
        perms.Should().Contain(Permissions.MachinesRead);
        perms.Should().Contain(Permissions.AuditRead);
    }

    [Fact]
    public void GetEffectivePermissions_Admin_ReturnsFifteenPermissions()
    {
        var perms = RbacService.GetEffectivePermissions(["Admin"]);
        perms.Should().HaveCount(15);
    }

    [Fact]
    public void GetEffectivePermissions_Auditor_ReturnsSixPermissions()
    {
        var perms = RbacService.GetEffectivePermissions(["Auditor"]);
        perms.Should().HaveCount(6);
    }

    [Fact]
    public void GetEffectivePermissions_MultipleOverlappingRoles_DeduplicatesPermissions()
    {
        // Both Viewer and Auditor have MachinesRead, PatchesRead, etc.
        var viewerPerms = RbacService.GetEffectivePermissions(["Viewer"]);
        var combined = RbacService.GetEffectivePermissions(["Viewer", "Auditor"]);

        // Auditor adds AuditExport but otherwise overlaps.
        combined.Should().Contain(Permissions.AuditExport);
        combined.Count.Should().BeGreaterThanOrEqualTo(viewerPerms.Count);
    }

    [Fact]
    public void GetEffectivePermissions_EmptyRoles_ReturnsEmpty()
    {
        RbacService.GetEffectivePermissions([]).Should().BeEmpty();
    }

    [Fact]
    public void GetEffectivePermissions_UnknownRole_ReturnsEmpty()
    {
        RbacService.GetEffectivePermissions(["Ghost"]).Should().BeEmpty();
    }

    // ── GetDefaultRoles ──

    [Fact]
    public void GetDefaultRoles_ReturnsFourRoles()
    {
        var roles = RbacService.GetDefaultRoles();
        roles.Should().HaveCount(4);
    }

    [Fact]
    public void GetDefaultRoles_ContainsExpectedRoleNames()
    {
        var roles = RbacService.GetDefaultRoles();
        var names = roles.Select(r => r.Name).ToList();

        names.Should().Contain("Viewer");
        names.Should().Contain("Operator");
        names.Should().Contain("Admin");
        names.Should().Contain("Auditor");
    }

    [Fact]
    public void GetDefaultRoles_EachRoleHasEmptyGuidId()
    {
        var roles = RbacService.GetDefaultRoles();
        roles.Should().AllSatisfy(r => r.RoleId.Should().Be(Guid.Empty));
    }

    [Fact]
    public void GetDefaultRoles_EachRoleHasNonEmptyPermissions()
    {
        var roles = RbacService.GetDefaultRoles();
        roles.Should().AllSatisfy(r => r.Permissions.Should().NotBeEmpty());
    }

    [Fact]
    public void GetDefaultRoles_AdminHasAllPermissions()
    {
        var roles = RbacService.GetDefaultRoles();
        var admin = roles.Single(r => r.Name == "Admin");

        admin.Permissions.Should().Contain(Permissions.AdminUsers);
        admin.Permissions.Should().Contain(Permissions.AdminSettings);
        admin.Permissions.Should().Contain(Permissions.MachinesRead);
        admin.Permissions.Should().Contain(Permissions.MachinesWrite);
    }
}
