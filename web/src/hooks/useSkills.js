"use client";

import { useEffect } from "react";
import { useDispatch, useSelector } from "react-redux";
import { fetchAvailableTools, fetchSkills } from "@/store/skillsSlice";

export function useSkills() {
  const dispatch = useDispatch();
  const { items, listStatus, availableTools, error } = useSelector((state) => state.skills);

  useEffect(() => {
    dispatch(fetchSkills());
    dispatch(fetchAvailableTools());
  }, [dispatch]);

  return { skills: items, status: listStatus, availableTools, error };
}
