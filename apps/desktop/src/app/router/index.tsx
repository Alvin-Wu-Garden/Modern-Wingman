import { createBrowserRouter } from 'react-router-dom'
import { ChatPage } from '@/features/chat/components/ChatPage'

export const router = createBrowserRouter([
  {
    path: '/',
    element: <ChatPage />,
  },
])
