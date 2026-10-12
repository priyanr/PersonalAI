import { redirect } from "next/navigation";

export default async function Page({ params }) {
  const { skillId } = await params;
  redirect(`/settings/skills/${skillId}`);
}
