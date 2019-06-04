﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Linq;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.E2E.Common;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Search
{
    extern alias E2ER4;

    [HttpIntegrationFixtureArgumentSets(DataStore.CosmosDb, Format.Json, FhirVersion.All)]
    public class CompositeSearchTests : SearchTestsBase<CompositeSearchTestFixture>
    {
        private const string ObservationWith1MinuteApgarScore = "ObservationWith1MinuteApgarScore";
        private const string ObservationWith20MinuteApgarScore = "ObservationWith20MinuteApgarScore";
        private const string ObservationWithTemperature = "ObservationWithTemperature";
        private const string ObservationWithTPMTDiplotype = "ObservationWithTPMTDiplotype";
        private const string ObservationWithTPMTHaplotypeOne = "ObservationWithTPMTHaplotypeOne";
        private const string ObservationWithBloodPressure = "ObservationWithBloodPressure";

        public CompositeSearchTests(CompositeSearchTestFixture fixture)
            : base(fixture)
        {
        }

        [Theory]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$10|http://unitsofmeasure.org|{score}", ObservationWith20MinuteApgarScore)]
        [InlineData("code-value-quantity=443849008$10|http://unitsofmeasure.org|{score}", ObservationWith20MinuteApgarScore)]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$10", ObservationWith20MinuteApgarScore)]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$10||{score}", ObservationWith20MinuteApgarScore)]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$eq10|http://unitsofmeasure.org|{score}", ObservationWith20MinuteApgarScore)]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$ne10|http://unitsofmeasure.org|{score}")]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$lt10|http://unitsofmeasure.org|{score}")]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$le10|http://unitsofmeasure.org|{score}", ObservationWith20MinuteApgarScore)]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$gt10|http://unitsofmeasure.org|{score}")]
        [InlineData("code-value-quantity=http://snomed.info/sct|443849008$ge10|http://unitsofmeasure.org|{score}", ObservationWith20MinuteApgarScore)]
        [InlineData("code-value-quantity=http://loinc.org|8310-5$39|http://unitsofmeasure.org|Cel", ObservationWithTemperature)]
        [InlineData("code-value-quantity=http://loinc.org|8331-1$39|http://unitsofmeasure.org|Cel", ObservationWithTemperature)]
        [InlineData("code-value-quantity=http://snomed.info/sct|56342008$39|http://unitsofmeasure.org|Cel", ObservationWithTemperature)]
        [InlineData("combo-code-value-quantity=http://loinc.org|9272-6$0|http://unitsofmeasure.org|{score}", ObservationWith1MinuteApgarScore)] // Match: Observation.code against Observation.valueQuantity
        [InlineData("combo-code-value-quantity=http://snomed.info/sct|169895004$0|http://unitsofmeasure.org|{score}", ObservationWith1MinuteApgarScore)] // Match: Observation.code against Observation.valueQuantity
        [InlineData("combo-code-value-quantity=85354-9$107")] // Not match: Observation.code against Observation.component[0].valueQuantity
        [InlineData("combo-code-value-quantity=8480-6$107", ObservationWithBloodPressure)] // Match: Observation.component[0].code against Observation.component[0].valueQuantity
        [InlineData("combo-code-value-quantity=8480-6$60")] // Not match: Observation.component[0].code against Observation.component[1].valueQuantity
        public async Task GivenACompositeSearchParameterWithTokenAndQuantity_WhenSearched_ThenCorrectBundleShouldBeReturned(string queryValue, params string[] expectedObservationNames)
        {
            await SearchAndValidate(queryValue, expectedObservationNames);
        }

        [Theory]
        [InlineData("code-value-string=http://snomed.info/sct|162806009$blue", "ObservationWithEyeColor")]
        [InlineData("code-value-string=162806009$blue", "ObservationWithEyeColor")]
        [InlineData("code-value-string=http://snomed.info/sct|$blue", "ObservationWithEyeColor")]
        [InlineData("code-value-string=http://snomed.info/sct|162806009$red")]
        public async Task GivenACompositeSearchParameterWithTokenAndString_WhenSearched_ThenCorrectBundleShouldBeReturned(string queryValue, params string[] expectedObservationNames)
        {
            await SearchAndValidate(queryValue, expectedObservationNames);
        }

        [Theory]
        [HttpIntegrationFixtureArgumentSets(fhirVersions: FhirVersion.Stu3)]
        [InlineData("related=Observation/example-TPMT-haplotype-one$http://hl7.org/fhir/observation-relationshiptypes|derived-from", ObservationWithTPMTDiplotype)]
        [InlineData("related=Observation/example-TPMT-haplotype-one$derived-from", ObservationWithTPMTDiplotype)]
        [InlineData("related=Sequence/example-TPMT-one$derived-from", ObservationWithTPMTHaplotypeOne)]
        [InlineData("related=Observation/example-TPMT-haplotype-one,Sequence/example-TPMT-one$derived-from", ObservationWithTPMTDiplotype, ObservationWithTPMTHaplotypeOne)]
        [InlineData("related=Observation/example-TPMT-haplotype-three$derived-from")]
        [InlineData("related=Observation/example-TPMT-haplotype-one$sequel-to")]
        public async Task GivenACompositeSearchParameterWithTokenAndReference_WhenSearched_ThenCorrectBundleShouldBeReturned(string queryValue, params string[] expectedObservationNames)
        {
            await SearchAndValidate(queryValue, expectedObservationNames);
        }

        [Theory]
        [InlineData("combo-code-value-concept=http://snomed.info/sct|443849008$http://loinc.org/la|LA6724-4")] // Not match: Observation.code against Observation.component[0].valueCodeableConcept.coding[0]
        [InlineData("combo-code-value-concept=443849008$http://loinc.org/la|LA6724-4")] // Not match: Observation.code (without system) against Observation.component[0].valueCodeableConcept.coding[0]
        [InlineData("combo-code-value-concept=http://snomed.info/sct|443849008$LA6724-4")] // Not match: Observation.code against Observation.component[0].valueCodeableConcept.coding[0] (without system)
        [InlineData("combo-code-value-concept=443849008$LA6724-4")] // Not match: Observation.code (without system) against Observation.component[0].valueCodeableConcept.coding[0] (without system)
        [InlineData("combo-code-value-concept=|443849008$http://loinc.org/la|LA6724-4")] // Not match: Observation.code (with explicit no system) against Observation.component[0].valueCodeableConcept
        [InlineData("combo-code-value-concept=http://snomed.info/sct|443849008$|LA6724-4")] // Not match: Observation.code against Observation.component[0].valueCodeableConcept.coding[0] (with explicit no system)
        [InlineData("combo-code-value-concept=http://snomed.info/sct|443849008$http://snomed.info/sct|443849008")] // Not match: Observation.code against Observation.code
        [InlineData("combo-code-value-concept=http://snomed.info/sct|443849008$http://snomed.info/sct|249227004")] // Not match: Observation.code against Observation.component[0].code
        [InlineData("combo-code-value-concept=http://snomed.info/sct|443849008$http:/acme.ped/apgarcolor|2")] // Not match: Observation.code against Observation.component[0].valueCodeableConcept.coding[1]
        [InlineData("combo-code-value-concept=http://snomed.info/sct|249227004$http://loinc.org/la|LA6724-4", ObservationWith20MinuteApgarScore)] // Match: Observation.component[0].code against Observation.component[0].valueCodeableConcept.coding[0]
        [InlineData("combo-code-value-concept=http://snomed.info/sct|249227004$http:/acme.ped/apgarcolor|2", ObservationWith20MinuteApgarScore)] // Match: Observation.component[1].code against Observation.component[0].valueCodeableConcept.coding[1]
        [InlineData("combo-code-value-concept=http://loinc.org/la|LA6724-4$http:/acme.ped/apgarcolor|2")] // Not match: Observation.component[0].valueCodeableConcept.coding[0] against Observation.component[0].valueCodeableConcept.coding[1]
        [InlineData("combo-code-value-concept=169895004$http://loinc.org/la|LA6725-1")] // Not match: Observation.code[1] against Observation.component[4].valueCodeableConcept.coding[0]
        public async Task GivenACompositeSearchParameterWithTokenAndToken_WhenSearched_ThenCorrectBundleShouldBeReturned(string queryValue, params string[] expectedObservationNames)
        {
            await SearchAndValidate(queryValue, expectedObservationNames);
        }

        private async Task SearchAndValidate(string queryValue, string[] expectedObservationNames)
        {
            ResourceElement bundle = await SearchAsync(queryValue);

            ResourceElement[] expected = expectedObservationNames.Select(name => Fixture.Observations[name]).ToArray();

            ValidateBundle(bundle, expected);
        }

        private async Task<ResourceElement> SearchAsync(string queryValue)
        {
            // Append the test session id.
            return await Client.SearchAsync(
                KnownResourceTypes.Observation,
                $"identifier={Fixture.TestSessionId}&{queryValue}");
        }
    }
}