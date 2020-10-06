# Shark Robot

This plugin adds Shark Robot functionality to HomeSeer 4. Unfortunately, there does not appear
to be a local LAN interface to the Shark Robot, so this plugin requires an Internet connection
and your cloud account credentials.

**This plugin has only been tested with the Shark IQ Robot.** I currently don't know whether
it will also work with the Shark ION Robot.

# Installation

This plugin is **not yet available** in the HomeSeer plugin updater.

# Configuration

Once the plugin is installed and running, the settings page can be accessed via
Plugins > Shark Robot > Settings. Enter your Shark cloud credentials and click Save. The status
at the top of the page will change to "Logging in...". Refresh the page after a few seconds to
update the status and make sure the status is "OK".

**Your account password is not stored.** After the plugin logs in, your password is discarded and
only the authentication tokens returned by the cloud service are stored. Therefore, if you change
your Shark cloud account login email, you will need to provide both your new email and your password,
even if your password did not change.

Once the plugin successfully logs into the Shark cloud service, it will automatically create
devices in HS4 for each robot in your cloud account.

# Removing a Shark Robot Device

If you need to remove a Shark Robot device, first disable the plugin on the Plugins page, then
manually delete whichever robot device(s) you want to remove and all of their features.

At the top-right of the Devices page is a button which looks like a checklist. Click this button to
enable bulk device editing. Then select the checkbox next to the Gateway device, which should also
automatically select all of its features. If the features are not automatically selected, manually
select each one. Then scroll back to the top of the page and select Bulk Action > Delete.
