# AirBridge ProGuard rules
# Added as needed when features are implemented

# Keep Hilt-generated classes
-keep class dagger.hilt.** { *; }
-keep class javax.inject.** { *; }

# Keep protocol message classes (used via reflection in tests)
-keep class com.airbridge.app.transport.protocol.** { *; }
-keep class com.airbridge.app.core.** { *; }
