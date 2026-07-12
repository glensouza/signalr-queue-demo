## Changes Made

- **Unified Button Styling:** Added global dark theme styles for buttons in `theme.css` so they apply properly to all Angular components, including internal-queue and public-checkin.
- **Display Board Layout:** Shifted the queue display to place 'Now Serving' on the left (smaller column) and the 'Waiting List' on the right. Moved the QR code from the right to the left.
- **Cancel Queue Entry functionality:** 
  - Added a new `Cancelled` state in the C# domain types.
  - Implemented `CancelAsync` in both SQLite and Table Storage Repositories.
  - Created `POST /queue/{id}/cancel` in `QueueEndpoints.cs`.
  - Updated the frontend `QueueApiService` to call the new cancel endpoint.
  - Updated the `position-view.ts` (the Kiosk 'Stop tracking' button) to cancel the entry on the backend before emitting the finished state, so it actually clears out of the staff queue.

Note: These changes are in the working directory for code review and have not been committed yet.
