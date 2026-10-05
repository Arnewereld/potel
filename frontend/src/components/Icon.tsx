import { Box, Truck, Package, Wrench, Users, FileText, Star, Briefcase, Folder, Heart, Home, Tag, type LucideProps } from 'lucide-react'

// Pictogrammen die je kunt kiezen voor een eigen module.
export const moduleIcons = { box: Box, truck: Truck, package: Package, wrench: Wrench, users: Users, file: FileText, star: Star, briefcase: Briefcase, folder: Folder, heart: Heart, home: Home, tag: Tag }

export function ModuleIcon({ name, ...props }: { name: string } & LucideProps) {
  const C = moduleIcons[name as keyof typeof moduleIcons] ?? Box
  return <C {...props} />
}
