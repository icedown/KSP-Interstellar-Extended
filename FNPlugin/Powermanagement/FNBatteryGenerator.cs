﻿using FNPlugin.Extensions;
using System;
using System.Collections.Generic;

namespace FNPlugin.Powermanagement
{
    [KSPModule("Reactor Power Generator")]
    class FNPowerGenerator : FNBatteryGenerator
    {
        public FNPowerGenerator() { canRecharge = false; }
    }

    [KSPModule("Battery Power Generator")]
    class FNBatteryGenerator : ResourceSuppliableModule
    {
        // configuration
        [KSPField(guiActiveEditor = true, guiName = "Maximum Power", guiUnits = " MW", guiFormat = "F3")]
        public double maxPower = 1;
        [KSPField]
        public bool canRecharge = true;
        [KSPField]
        public double efficiency = 1;
        [KSPField]
        public string inputResources = "KilowattHour";
        [KSPField]
        public string inputConversionRates = "2.77777777777e-1";
        [KSPField]
        public string outputConversionRates = "";
        [KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#LOC_KSPIE_Reactor_electricPriority"), UI_FloatRange(stepIncrement = 1, maxValue = 5, minValue = 0)]
        public float electricSupplyPriority = 5;
        [KSPField(guiActive = true, guiName = "Spare MW Capacity",  guiUnits = " MW", guiFormat = "F3")]
        public double spareResourceCapacity;
        [KSPField(guiActive = true, guiName = "Remaining supply lifetime", guiUnits = " s", guiFormat = "F0")]
        public double batterySupplyRemaining;
        [KSPField(guiActiveEditor = true, guiName = "Maximum Power", guiUnits = " MW", guiFormat = "F3")]
        public double currentMaxPower = 1;
        [KSPField(guiActive = true, guiName = "Power Supply", guiUnits = " MW", guiFormat = "F3")]
        public double powerSupply;
        [KSPField(guiActive = true, guiName = "Wasteheat", guiUnits = " MJ", guiFormat = "F3")]
        public double wasteheat;

        [KSPField(isPersistant = true, guiActive = true, guiName = "#LOC_KSPIE_Generator_electricPowerNeeded", guiUnits = " MW", guiFormat = "F4")]
        public double electrical_power_currently_needed;
        [KSPField(isPersistant = true, guiActive = true, guiName = "#LOC_KSPIE_Generator_powerControl"), UI_FloatRange(stepIncrement = 0.5f, maxValue = 100f, minValue = 0.5f)]
        public float powerPercentage = 100;

        // privates
        List<string> inputResourceNames;
        List<double> inputResourceRate;

        double wasteheatRatio;
        double overheatingMultiplier;
        double fuelRatio;
        double powerSurplus;
        double currentUnfilledResourceDemand;
        double currentRequestedConsumptionRate;
        double currentFixedConsumption;
        double requestedPower;
        double consumedPower;

        public override void OnStart(StartState state)
        {
            if (state == StartState.Editor) return;

            string[] resources_to_supply = { ResourceManager.FNRESOURCE_MEGAJOULES };
            this.resources_to_supply = resources_to_supply;
            base.OnStart(state);

            part.force_activate();

            inputResourceNames = ParseTools.ParseNames(inputResources);
            inputResourceRate = ParseTools.ParseDoubles(inputConversionRates);
        }

        public override void OnFixedUpdateResourceSuppliable(double fixedDeltaTime)
        {
            fuelRatio = double.MaxValue;

            wasteheatRatio = getResourceBarRatio(ResourceManager.FNRESOURCE_WASTEHEAT);
            overheatingMultiplier = wasteheatRatio < efficiency ? 1 : 1 - (wasteheatRatio - efficiency);

            currentMaxPower = Math.Round(powerPercentage * 0.01, 2) * maxPower;
            currentUnfilledResourceDemand = Math.Max(0, GetCurrentUnfilledResourceDemand(ResourceManager.FNRESOURCE_MEGAJOULES));
            spareResourceCapacity = getSpareResourceCapacity(ResourceManager.FNRESOURCE_MEGAJOULES);

            electrical_power_currently_needed = overheatingMultiplier * Math.Min(currentUnfilledResourceDemand + spareResourceCapacity, currentMaxPower) / efficiency;

            for (var i = 0; i < inputResourceNames.Count; i++)
            {
                var currentResourceName = inputResourceNames[i];
                var currentBatteryResource = part.Resources[currentResourceName];

                if (currentBatteryResource != null)
                {
                    var currentInputConversionRate = inputResourceRate[i];
                    currentRequestedConsumptionRate = electrical_power_currently_needed * currentInputConversionRate;
                    currentFixedConsumption = currentRequestedConsumptionRate * fixedDeltaTime;
                    var currentFuelRatio = currentFixedConsumption != 0 ? currentBatteryResource.amount / currentFixedConsumption : 0;

                    if (currentFuelRatio < fuelRatio)
                        fuelRatio = currentFuelRatio;

                    var currentBatterySupplyRemaining = currentFuelRatio / fixedDeltaTime / 1000;
                    if (currentBatterySupplyRemaining < batterySupplyRemaining)
                        batterySupplyRemaining = currentBatterySupplyRemaining;

                    currentBatteryResource.amount = Math.Max(0, currentBatteryResource.amount - currentFixedConsumption);
                }
            }

            var effectiveFuelRatio = Math.Min(1, fuelRatio);
            var rawPowerSupply = effectiveFuelRatio * electrical_power_currently_needed;
            var inefficiency = 1 - efficiency;

            powerSupply = efficiency * rawPowerSupply;
            wasteheat = inefficiency * rawPowerSupply;

            if (powerSupply != 0)
            {
                powerSurplus = 0;
                requestedPower = 0;
                consumedPower = 0;
                supplyFNResourcePerSecondWithMax(powerSupply, currentMaxPower * efficiency * effectiveFuelRatio, ResourceManager.FNRESOURCE_MEGAJOULES);
                if (inefficiency > 0)
                    supplyFNResourcePerSecondWithMax(wasteheat, currentMaxPower * inefficiency * effectiveFuelRatio, ResourceManager.FNRESOURCE_WASTEHEAT);
            }
            else if (canRecharge) 
            {
                powerSurplus = GetCurrentSurplus(ResourceManager.FNRESOURCE_MEGAJOULES);

                if (powerSurplus > 0)
                {
                    for (var i = 0; i < inputResourceNames.Count; i++)
                    {
                        var currentResourceName = inputResourceNames[i];
                        var currentBatteryResource = part.Resources[currentResourceName];

                        if (currentBatteryResource != null)
                        {
                            var spareRoom = Math.Max(0, currentBatteryResource.maxAmount - currentBatteryResource.amount);
                            var currentInputConversionRate = inputResourceRate[i];
                            var maxPowerNeeded = spareRoom / currentInputConversionRate / fixedDeltaTime;
                            requestedPower = Math.Min(powerSurplus * 0.995, maxPowerNeeded);
                            consumedPower = consumeFNResourcePerSecond(requestedPower, ResourceManager.FNRESOURCE_MEGAJOULES);

                            currentRequestedConsumptionRate = consumedPower * currentInputConversionRate;
                            currentFixedConsumption = currentRequestedConsumptionRate * fixedDeltaTime;
                            currentBatteryResource.amount = currentBatteryResource.amount + currentFixedConsumption;
                        }
                    }
                }
            }
            else
            {
                // nothing yet
            }
        }

        public override int getPowerPriority()
        {
            return 5;
        }

        public override int getSupplyPriority()
        {
            return (int)electricSupplyPriority;
        }
    }
}
