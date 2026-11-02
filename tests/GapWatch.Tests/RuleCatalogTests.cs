using System.Text.RegularExpressions;
using GapWatch.Core.Findings;
using Shouldly;

namespace GapWatch.Tests;

public sealed class RuleCatalogTests
{
    [Test]
    public void Rule_ids_are_unique_and_well_formed()
    {
        var ids = RuleCatalog.All.Select(r => r.Id).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count);
        ids.All(id => Regex.IsMatch(id, @"^GW\d{3}$")).ShouldBeTrue();
    }

    [Test]
    public void Rule_names_are_unique_and_every_rule_is_described()
    {
        RuleCatalog.All.Select(r => r.Name).Distinct().Count().ShouldBe(RuleCatalog.All.Count);
        RuleCatalog.All.All(r => r.Description.Length > 20).ShouldBeTrue();
    }

    [Test]
    public void Rules_are_listed_in_id_order()
    {
        var ids = RuleCatalog.All.Select(r => r.Id).ToList();

        string.Join(",", ids).ShouldBe(string.Join(",", ids.OrderBy(i => i, StringComparer.Ordinal)));
    }

    [Test]
    public void The_catalog_contains_the_planned_rules()
    {
        // Guards against accidentally losing rules to a refactoring; update deliberately when rules are added.
        RuleCatalog.All.Count.ShouldBe(28);
    }
}
