# BDSM

How to use:
- Copy UserConfiguration.example.yaml to UserConfiguration.yaml and configure. Be sure to change the mod path to your game's mod directory, and comment/uncomment paths to choose which packs you want to update.
- Optionally, you can add nlog.config to configure custom logging. See https://github.com/NLog/NLog/wiki/Configuration-file for more information. Beginning with 0.2.3, nlog.config is no longer used for the default configuration and any versions from previous releases should be deleted.
- Currently, only HS2 is tested and AIS will probably be fine (you can add the AIS exclusive folder to the configuration). This is not meant for any other Illusion game, though it may work given the proper directory names in the configuration.