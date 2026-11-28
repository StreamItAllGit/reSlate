## Summary
- Describe what changed and why.

## Verification
- [ ] `$env:APP_ISOLATED_TEST = "1"`
- [ ] `dotnet build Dev\src\reStation.slnx -c Release /warnaserror` (`0` warnings)
- [ ] `dotnet test Dev\src\reStation.slnx -c Release --no-build`
- [ ] `dotnet build Dev\src\reStation.slnx -c Debug -p:ReStationTestAutomation=true /warnaserror` (`0` warnings)
- [ ] `dotnet test Dev\src\reStation.slnx -c Debug -p:ReStationTestAutomation=true --no-build`
- [ ] Zero copyrighted ROM/disc/BIOS files, PII paths, or Release `Foundation.Automation` references introduced
