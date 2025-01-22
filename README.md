# DNN Azure App Service Adapter
### Latest release [![Latest release](docs/images/BadgeRelease.svg)](https://github.com/intelequia/dnn.azureappservice/releases)


<a name="requirements"></a>
## Requirements
* **DNN Platform 9.3.0 or later**

<a name="overview"></a>
## Overview
The DNN Azure AppService Adapter is library for DNN Platform to integrate different AppService features into DNN Platform, such as scheduled tasks and other features. 

## Known issues
* When installing a newer version of the module, the service bus topicName attribute under the "caching" node in the web.config file is being overwritten with the "dnntopic" text. After reinstalling, please manually update your web.config with your previous topic name.

<a name="building"></a>
# Building the solution
### Requirements
* Visual Studio 2017 or later (download from https://www.visualstudio.com/downloads/)

### Build the module
Now you can build the solution by opening the file `src\DotNetNuke.Azure.AppService.sln` in Visual Studio. Building the solution in "Release", will generate the package with the installation zip file, created under the "\releases" folder.
